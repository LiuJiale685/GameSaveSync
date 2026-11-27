// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameSaveSync.Core.Interfaces;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using Serilog;

namespace GameSaveSync.Desktop.Services;

/// <summary>
/// 游戏进程监控（方案 6.2 / 6.3 / T08）：
/// 1) 实现 <see cref="IGameProcessChecker"/>（进程名去 .exe 后缀、忽略大小写比较）；
/// 2) 每 5 秒轮询配置了进程名的游戏，检测“运行 → 退出”转换，
///    延迟 15 秒（允许游戏完成最后一次写盘）后自动执行备份回调；
///    备份失败只记日志，不打扰用户。
/// </summary>
public sealed class GameProcessMonitorService : IGameProcessChecker, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitBackupDelay = TimeSpan.FromSeconds(15);

    /// <summary>自动备份回调（由 App 启动时接到门面服务的 BackupNowAsync）。</summary>
    public Func<string, CancellationToken, Task<CommitInfo>>? BackupAsync { get; set; }

    /// <summary>
    /// 每次轮询回调（gameId, 当前是否运行）。用于游戏时长会话记录（开始 / 心跳 / 结束），
    /// 由 App 启动时接到 <see cref="PlaySessionRecorder.OnTickAsync"/>。
    /// </summary>
    public Func<string, bool, CancellationToken, Task>? SessionTickAsync { get; set; }

    /// <summary>游戏运行状态变化（gameId, isRunning）；轮询发现变化或用户启动/停止游戏时触发。</summary>
    public event Action<string, bool>? GameStateChanged;

    private readonly object _sync = new();
    private Dictionary<string, WatchedGame> _games = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>启动游戏进程；<paramref name="executablePath"/> 指向 exe 文件。</summary>
    public Process LaunchGame(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("游戏可执行文件不存在，请检查配置。", executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = true,
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("启动游戏进程失败。");
        Log.Information("已启动游戏进程：{Exe} (PID {Pid})", executablePath, process.Id);
        return process;
    }

    /// <summary>
    /// 游戏启动后自动探测真实进程名（处理启动器拉起子进程、进程重建等情况）：
    /// 在 <paramref name="timeout"/> 内轮询系统进程，匹配“与启动的 exe 同名”或
    /// “可执行文件位于游戏 exe 所在目录下”的进程，只返回一个进程名（带 .exe 后缀）。
    /// 探测不到返回 null（不报错）。
    /// </summary>
    public async Task<string?> DetectProcessNamesAsync(
        string executablePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var exeName = ProcessNameNormalizer.Normalize(Path.GetFileName(executablePath));
        var gameDir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        var deadline = DateTime.UtcNow + timeout;

        // 先给游戏/启动器一点启动时间
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        while (DateTime.UtcNow < deadline)
        {
            var candidates = await Task.Run(() => ScanRunningProcesses(exeName, gameDir), cancellationToken)
                .ConfigureAwait(false);
            var selected = SelectBestProcessName(candidates);
            if (selected is not null)
            {
                // 再等一轮，尽量把启动器随后拉起的真正游戏进程也纳入后重新挑选
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
                var extra = await Task.Run(() => ScanRunningProcesses(exeName, gameDir), cancellationToken)
                    .ConfigureAwait(false);
                selected = SelectBestProcessName(candidates.Concat(extra)) ?? selected;

                Log.Information("自动识别到游戏进程名：{Name}", selected);
                return selected;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        Log.Information("未能自动识别游戏进程名（exe={Exe}，超时 {Timeout} 秒）。",
            executablePath, timeout.TotalSeconds);
        return null;
    }

    /// <summary>
    /// 立即扫描一次：返回“与指定 exe 同名”或“可执行文件位于该 exe 所在目录”的进程名
    /// （带 .exe 后缀，最多一个）。用于编辑对话框中的手动“自动识别”。
    /// </summary>
    public static string? DetectRunningProcessNames(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var exeName = ProcessNameNormalizer.Normalize(Path.GetFileName(executablePath));
        var gameDir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        return SelectBestProcessName(ScanRunningProcesses(exeName, gameDir));
    }

    private readonly record struct ProcessCandidate(
        string Name, bool MatchesExe, bool HasMainWindow, DateTime StartTimeUtc);

    /// <summary>
    /// 从候选进程里只挑一个：优先与游戏 exe 同名的进程（游戏程序本身）；
    /// 否则优先有主窗口的进程；仍无法区分时取启动时间最早的一个。
    /// </summary>
    private static string? SelectBestProcessName(IEnumerable<ProcessCandidate> candidates)
    {
        // 同名进程（多个 PID）只保留启动最早的一个
        var distinct = candidates
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => c.StartTimeUtc).First())
            .ToList();
        if (distinct.Count == 0)
            return null;

        var exeMatch = distinct.Where(c => c.MatchesExe).OrderBy(c => c.StartTimeUtc).ToList();
        if (exeMatch.Count > 0)
            return exeMatch[0].Name;

        var withWindow = distinct.Where(c => c.HasMainWindow).OrderBy(c => c.StartTimeUtc).ToList();
        if (withWindow.Count > 0)
            return withWindow[0].Name;

        return distinct.OrderBy(c => c.StartTimeUtc).First().Name;
    }

    private static List<ProcessCandidate> ScanRunningProcesses(string exeName, string? gameDir)
    {
        var found = new List<ProcessCandidate>();
        var dirPrefix = string.IsNullOrEmpty(gameDir)
            ? null
            : gameDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var normalized = ProcessNameNormalizer.Normalize(process.ProcessName);
                    if (normalized.Length == 0)
                        continue;

                    var matchesExe = string.Equals(normalized, exeName, StringComparison.OrdinalIgnoreCase);
                    if (!matchesExe)
                    {
                        if (dirPrefix is null)
                            continue;

                        string? filePath;
                        try
                        {
                            filePath = process.MainModule?.FileName;
                        }
                        catch
                        {
                            // 受保护进程 / 位数不同时无法读取模块路径，忽略即可
                            continue;
                        }
                        if (filePath is null ||
                            !filePath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    bool hasWindow;
                    try
                    {
                        hasWindow = process.MainWindowHandle != IntPtr.Zero;
                    }
                    catch
                    {
                        hasWindow = false;
                    }

                    DateTime startTime;
                    try
                    {
                        startTime = process.StartTime.ToUniversalTime();
                    }
                    catch
                    {
                        startTime = DateTime.MaxValue;
                    }

                    found.Add(new ProcessCandidate(normalized, matchesExe, hasWindow, startTime));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "读取进程信息失败（已忽略）。");
                }
            }
        }
        return found;
    }

    /// <summary>停止游戏进程（先尝试优雅关闭窗口，超时后强制结束）。返回结束的进程数。</summary>
    public int StopGame(IReadOnlyList<string> processNames)
    {
        var targets = BuildTargetSet(processNames);
        if (targets.Count == 0)
            return 0;

        var matched = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (targets.Contains(NormalizeProcessName(process.ProcessName)))
                matched.Add(process);
            else
                process.Dispose();
        }

        var closed = 0;
        foreach (var process in matched)
        {
            try
            {
                // 优雅关闭：请求关闭主窗口，等待最多 5 秒
                if (process.CloseMainWindow() && process.WaitForExit(5000))
                {
                    closed++;
                    continue;
                }
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                closed++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "结束进程 {Name} (PID {Pid}) 失败。", process.ProcessName, process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }

        Log.Information("已停止游戏进程 {Count} 个。", closed);
        return closed;
    }

    /// <summary>查询某游戏当前是否正在运行（按配置的进程名）。</summary>
    public bool IsGameRunning(IReadOnlyList<string> processNames) => IsAnyRunning(processNames);

    /// <summary>查询指定游戏当前运行状态；游戏未在监控列表或未配置进程名时返回 false。</summary>
    public bool GetRunningState(string gameId)
    {
        lock (_sync)
            return _games.TryGetValue(gameId, out var game) && game.WasRunning;
    }

    /// <inheritdoc cref="IGameProcessChecker.IsAnyRunningAsync" />
    public Task<bool> IsAnyRunningAsync(
        IReadOnlyList<string> processNames, CancellationToken cancellationToken = default)
        => Task.Run(() => IsAnyRunning(processNames), cancellationToken);

    /// <summary>以最新游戏配置替换监控列表（保留已知运行状态，避免误触发）。</summary>
    public void UpdateGames(IReadOnlyList<LocalGameConfig> games)
    {
        lock (_sync)
        {
            var updated = new Dictionary<string, WatchedGame>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games)
            {
                _games.TryGetValue(game.GameId, out var previous);
                updated[game.GameId] = new WatchedGame(
                    game.GameId,
                    game.ProcessNames,
                    game.AutoBackup,
                    wasRunning: previous?.WasRunning ?? false);
            }
            _games = updated;
        }
    }

    /// <summary>立即刷新一次所有游戏状态（UI 加载列表后调用，触发状态事件）。</summary>
    public Task RefreshStatesAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => PollOnceAsync(cancellationToken), cancellationToken);

    /// <summary>启动轮询循环（每 5 秒）。</summary>
    public void Start()
    {
        if (_loopTask is not null || _disposed) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
        Log.Information("进程监控已启动：每 {Poll} 秒轮询，游戏退出后 {Delay} 秒自动备份。",
            PollInterval.TotalSeconds, ExitBackupDelay.TotalSeconds);
    }

    /// <summary>停止轮询循环并等待退出。</summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* 轮询循环内部已吞掉取消异常 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "进程监控轮询异常。");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        List<WatchedGame> snapshot;
        lock (_sync)
            snapshot = _games.Values.ToList();

        foreach (var game in snapshot)
        {
            if (game.ProcessNames.Count == 0)
                continue;
            try
            {
                var running = IsAnyRunning(game.ProcessNames);
                if (game.WasRunning && !running && game.AutoBackup)
                {
                    Log.Information("检测到游戏 {GameId} 进程退出，{Delay} 秒后自动备份。",
                        game.GameId, ExitBackupDelay.TotalSeconds);
                    _ = DelayedBackupAsync(game.GameId, ct);
                }
                if (game.WasRunning != running)
                {
                    game.WasRunning = running;
                    RaiseStateChanged(game.GameId, running);
                }

                // 游戏时长会话记录：每次轮询都回调（开始 / 心跳 / 结束由记录器判定）
                var sessionTick = SessionTickAsync;
                if (sessionTick is not null)
                    _ = RecordSessionTickAsync(sessionTick, game.GameId, running, ct);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "轮询游戏 {GameId} 进程状态失败。", game.GameId);
            }
        }
    }

    /// <summary>会话记录回调（异常只记日志，不影响监控循环）。</summary>
    private static async Task RecordSessionTickAsync(
        Func<string, bool, CancellationToken, Task> tick, string gameId, bool running, CancellationToken ct)
    {
        try
        {
            await tick(gameId, running, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 程序退出时取消，正常。
        }
        catch (Exception ex)
        {
            Log.Error(ex, "记录游戏 {GameId} 运行会话失败。", gameId);
        }
    }

    private async Task DelayedBackupAsync(string gameId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ExitBackupDelay, ct).ConfigureAwait(false);
            var backup = BackupAsync;
            if (backup is null || ct.IsCancellationRequested)
                return;
            var commit = await backup(gameId, ct).ConfigureAwait(false);
            Log.Information("游戏 {GameId} 退出自动备份完成：commit {CommitId}。",
                gameId, commit.CommitId.Length >= 8 ? commit.CommitId[..8] : commit.CommitId);
        }
        catch (OperationCanceledException)
        {
            // 程序退出时取消，正常。
        }
        catch (Exception ex)
        {
            // 备份失败只记日志（方案 6.3）。
            Log.Error(ex, "游戏 {GameId} 退出自动备份失败。", gameId);
        }
    }

    private static bool IsAnyRunning(IReadOnlyList<string> processNames)
    {
        var targets = BuildTargetSet(processNames);
        if (targets.Count == 0)
            return false;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (targets.Contains(NormalizeProcessName(process.ProcessName)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>构造规范化后的进程名集合（配置为 name.exe，比较时去掉 .exe）。</summary>
    private static HashSet<string> BuildTargetSet(IReadOnlyList<string> processNames)
        => processNames
            .Select(ProcessNameNormalizer.ToProcessName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>触发状态变化事件（订阅方异常不影响监控循环）。</summary>
    private void RaiseStateChanged(string gameId, bool isRunning)
    {
        var handler = GameStateChanged;
        if (handler is null)
            return;
        try
        {
            handler(gameId, isRunning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "游戏 {GameId} 状态变化事件处理失败。", gameId);
        }
    }

    /// <summary>去掉 .exe 后缀（Process.ProcessName 不含后缀），比较不区分大小写。</summary>
    private static string NormalizeProcessName(string name)
        => ProcessNameNormalizer.ToProcessName(name);

    private sealed class WatchedGame(
        string gameId,
        IReadOnlyList<string> processNames,
        bool autoBackup,
        bool wasRunning)
    {
        public string GameId { get; } = gameId;
        public IReadOnlyList<string> ProcessNames { get; } = processNames;
        public bool AutoBackup { get; } = autoBackup;
        public bool WasRunning { get; set; } = wasRunning;
    }
}
