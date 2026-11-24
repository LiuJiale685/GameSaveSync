using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameSaveSync.Core.Interfaces;
using GameSaveSync.Core.Models;
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
            }
            catch (Exception ex)
            {
                Log.Error(ex, "轮询游戏 {GameId} 进程状态失败。", game.GameId);
            }
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

    /// <summary>构造规范化后的进程名集合（去 .exe、去空白、忽略大小写）。</summary>
    private static HashSet<string> BuildTargetSet(IReadOnlyList<string> processNames)
        => processNames
            .Select(NormalizeProcessName)
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

    /// <summary>去掉 .exe 后缀并修剪空白（比较不区分大小写）。</summary>
    private static string NormalizeProcessName(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Trim();
    }

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
