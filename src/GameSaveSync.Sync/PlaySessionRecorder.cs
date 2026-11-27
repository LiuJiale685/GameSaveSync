// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameSaveSync.Core.Services;
using GameSaveSync.Data;
using GameSaveSync.Data.Repositories;
using Serilog;

namespace GameSaveSync.Sync;

/// <summary>
/// 游戏运行会话记录器（方案 PLAYTIME-PLAN）：
/// 由进程监控每次轮询回调，按“运行 → 未运行”转换维护一条 PlaySessions 记录：
/// 开始（插入）、心跳（更新 LastSeenAt）、结束（回填 EndedAt 与时长）。
/// 启动时收尾崩溃残留会话（Interrupted），退出时收尾仍在运行的游戏（AppClosed）。
/// 时间精度取决于轮询间隔（5 秒）。
/// </summary>
public sealed class PlaySessionRecorder
{
    private readonly PlaySessionRepository _repo;
    private readonly Func<string> _now;
    private readonly SemaphoreSlim _gate = new(1, 1); // 串行化：避免并发轮询重复开始会话
    private readonly Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);

    public PlaySessionRecorder(PlaySessionRepository repo, Func<string>? nowProvider = null)
    {
        _repo = repo;
        _now = nowProvider ?? UtcClock.Now;
    }

    /// <summary>会话结束后回调（用于把本机会话发布到共享仓库，实现跨设备时长累加）。</summary>
    public Func<string, CancellationToken, Task>? SessionFinishedAsync { get; set; }

    /// <summary>启动时调用：把上次崩溃遗留的进行中会话按 LastSeenAt 收尾。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            await _repo.CloseOrphansAsync(ct).ConfigureAwait(false);
            lock (_active)
                _active.Clear();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动时收尾残留游戏会话失败。");
        }
    }

    /// <summary>退出时调用：仍在运行的游戏会话记到当前时刻。</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            await _repo.CloseAllAsync(_now(), PlaySessionEndReason.AppClosed, ct).ConfigureAwait(false);
            string[] runningGames;
            lock (_active)
            {
                runningGames = _active.Keys.ToArray();
                _active.Clear();
            }
            foreach (var gameId in runningGames)
                await NotifySessionFinishedAsync(gameId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出时收尾游戏会话失败。");
        }
    }

    /// <summary>监控轮询回调：<paramref name="isRunning"/> 为本次观察到的运行状态。</summary>
    public async Task OnTickAsync(string gameId, bool isRunning, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _now();
            string? active;
            lock (_active)
                _active.TryGetValue(gameId, out active);

            if (isRunning && active is null)
            {
                var sessionId = Guid.NewGuid().ToString("D");
                await _repo.InsertAsync(sessionId, gameId, now, ct).ConfigureAwait(false);
                lock (_active)
                    _active[gameId] = sessionId;
                Log.Information("游戏 {GameId} 开始运行，会话 {SessionId} 已记录。", gameId, sessionId);
            }
            else if (isRunning && active is not null)
            {
                await _repo.TouchLastSeenAsync(active, now, ct).ConfigureAwait(false);
            }
            else if (!isRunning && active is not null)
            {
                await _repo.FinishAsync(active, now, PlaySessionEndReason.NormalExit, ct).ConfigureAwait(false);
                lock (_active)
                    _active.Remove(gameId);
                Log.Information("游戏 {GameId} 已退出，会话 {SessionId} 结束。", gameId, active);
                await NotifySessionFinishedAsync(gameId, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>触发会话结束回调；失败只记日志，不影响会话记录本身。</summary>
    private async Task NotifySessionFinishedAsync(string gameId, CancellationToken ct)
    {
        if (SessionFinishedAsync is null)
            return;
        try
        {
            await SessionFinishedAsync(gameId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "发布游戏 {GameId} 会话到共享仓库失败，将在下次统计时重试。", gameId);
        }
    }
}
