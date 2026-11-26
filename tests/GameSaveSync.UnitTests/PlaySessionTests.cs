// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Data;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.UnitTests;

/// <summary>
/// 游戏时长会话记录（方案 PLAYTIME-PLAN）：
/// Recorder 状态机（开始 / 心跳 / 结束 / 退出收尾 / 崩溃恢复）与 Repository 聚合统计。
/// </summary>
public sealed class PlaySessionTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "gss-playsession-" + Guid.NewGuid().ToString("N") + ".db");

    private SqliteDatabase _db = null!;
    private PlaySessionRepository _repo = null!;
    private DateTimeOffset _now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private string NowText => _now.UtcDateTime.ToString(
        "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync();
        _repo = new PlaySessionRepository(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { File.Delete(_dbPath); } catch { /* 临时文件清理失败忽略 */ }
    }

    private PlaySessionRecorder CreateRecorder() => new(_repo, () => NowText);

    private void Advance(TimeSpan span) => _now = _now.Add(span);

    private const string GameId = "game-1";

    [Fact]
    public async Task RunningThenExit_RecordsSingleFinishedSession()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync();

        await recorder.OnTickAsync(GameId, true);          // 开始
        Advance(TimeSpan.FromMinutes(30));
        await recorder.OnTickAsync(GameId, true);          // 心跳
        Advance(TimeSpan.FromMinutes(10));
        await recorder.OnTickAsync(GameId, false);         // 结束

        var stats = await _repo.GetStatsAsync(GameId);
        Assert.Equal(1, stats.SessionCount);
        Assert.Equal(40 * 60, stats.TotalSeconds);
        Assert.Equal(PlaySessionEndReason.NormalExit,
            (await _repo.GetRecentSessionsAsync(GameId))[0].EndReason);
        Assert.Equal(0, await _repo.CountRunningAsync());
    }

    [Fact]
    public async Task RepeatedTicks_WhileRunning_DoNotCreateDuplicateSessions()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync();

        await recorder.OnTickAsync(GameId, true);
        Advance(TimeSpan.FromSeconds(5));
        await recorder.OnTickAsync(GameId, true);
        Advance(TimeSpan.FromSeconds(5));
        await recorder.OnTickAsync(GameId, true);

        Assert.Equal(1, await _repo.CountRunningAsync());
    }

    [Fact]
    public async Task ShortSession_IsRecordedButExcludedFromStats()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync();

        await recorder.OnTickAsync(GameId, true);
        Advance(TimeSpan.FromSeconds(30));
        await recorder.OnTickAsync(GameId, false);

        var stats = await _repo.GetStatsAsync(GameId);
        Assert.False(stats.HasData);
        Assert.Equal(0, stats.SessionCount);

        // 数据仍保留在表中（CSV 导出可见）
        Assert.Single(await _repo.GetAllSessionsAsync());
    }

    [Fact]
    public async Task AppClosed_ClosesRunningSessionAtExitTime()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync();

        await recorder.OnTickAsync(GameId, true);
        Advance(TimeSpan.FromMinutes(90));
        await recorder.StopAsync(); // 模拟 App 退出，游戏仍在运行

        var sessions = await _repo.GetRecentSessionsAsync(GameId);
        var session = Assert.Single(sessions);
        Assert.Equal(PlaySessionEndReason.AppClosed, session.EndReason);
        Assert.Equal(90 * 60, session.DurationSeconds);
        Assert.Equal(0, await _repo.CountRunningAsync());
    }

    [Fact]
    public async Task CrashOrphan_IsClosedAtLastSeenOnStartup()
    {
        // 模拟上次运行留下的进行中会话（有 StartedAt / LastSeenAt，无 EndedAt）
        await _repo.InsertAsync("s-orphan", GameId, "2026-01-01T10:00:00Z");
        await _repo.TouchLastSeenAsync("s-orphan", "2026-01-01T10:25:00Z");
        Assert.Equal(1, await _repo.CountRunningAsync());

        var recorder = CreateRecorder();
        await recorder.StartAsync(); // 启动时应收尾孤儿会话

        var session = Assert.Single(await _repo.GetRecentSessionsAsync(GameId));
        Assert.Equal(PlaySessionEndReason.Interrupted, session.EndReason);
        Assert.Equal("2026-01-01T10:25:00Z", session.EndedAt);
        Assert.Equal(25 * 60, session.DurationSeconds);
        Assert.Equal(0, await _repo.CountRunningAsync());
    }

    [Fact]
    public async Task Stats_ReportsFirstAndLastPlayTime()
    {
        // 两个会话：早的 20 分钟，晚的 3 小时
        await _repo.InsertAsync("s1", GameId, "2026-01-01T09:00:00Z");
        await _repo.FinishAsync("s1", "2026-01-01T09:20:00Z", PlaySessionEndReason.NormalExit);
        await _repo.InsertAsync("s2", GameId, "2026-01-03T20:00:00Z");
        await _repo.FinishAsync("s2", "2026-01-03T23:00:00Z", PlaySessionEndReason.NormalExit);

        var stats = await _repo.GetStatsAsync(GameId);

        Assert.Equal(2, stats.SessionCount);
        Assert.Equal(20 * 60 + 3 * 3600, stats.TotalSeconds);
        Assert.Equal((20 * 60 + 3 * 3600) / 2, stats.AverageSeconds);
        Assert.Equal(3 * 3600, stats.LongestSeconds);
        Assert.Equal("2026-01-01T09:00:00Z", stats.FirstPlayedAt);
        Assert.Equal("2026-01-03T23:00:00Z", stats.LastPlayedAt);
    }

    [Fact]
    public async Task Stats_EmptyWhenNoSessions()
    {
        var stats = await _repo.GetStatsAsync("no-such-game");
        Assert.False(stats.HasData);
        Assert.Equal(0, stats.SessionCount);
        Assert.Equal(0, stats.TotalSeconds);
        Assert.Null(stats.FirstPlayedAt);
        Assert.Null(stats.LastPlayedAt);
    }

    [Fact]
    public async Task Finish_WithEndBeforeStart_ClampsDurationToZero()
    {
        await _repo.InsertAsync("s-clamp", GameId, "2026-01-01T12:00:00Z");
        await _repo.FinishAsync("s-clamp", "2026-01-01T11:00:00Z", PlaySessionEndReason.NormalExit);

        var session = Assert.Single(await _repo.GetRecentSessionsAsync(GameId));
        Assert.Equal(0, session.DurationSeconds);
    }

    [Fact]
    public async Task RecentSessions_OrdersByStartDescending_AndLimits()
    {
        for (var i = 1; i <= 5; i++)
        {
            var id = "s" + i;
            var start = $"2026-01-0{i}T10:00:00Z";
            var end = $"2026-01-0{i}T11:00:00Z";
            await _repo.InsertAsync(id, GameId, start);
            await _repo.FinishAsync(id, end, PlaySessionEndReason.NormalExit);
        }

        var recent = await _repo.GetRecentSessionsAsync(GameId, topN: 3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("s5", recent[0].SessionId);
        Assert.Equal("s3", recent[2].SessionId);
    }

    [Fact]
    public async Task CountFinishedSessions_IgnoresRunningSessions()
    {
        Assert.Equal(0, await _repo.CountFinishedSessionsAsync(GameId));

        await _repo.InsertAsync("s-done", GameId, "2026-01-01T10:00:00Z");
        await _repo.FinishAsync("s-done", "2026-01-01T11:00:00Z", PlaySessionEndReason.NormalExit);
        await _repo.InsertAsync("s-running", GameId, "2026-01-01T12:00:00Z");

        Assert.Equal(1, await _repo.CountFinishedSessionsAsync(GameId));
        Assert.Equal(0, await _repo.CountFinishedSessionsAsync("no-such-game"));
    }
}
