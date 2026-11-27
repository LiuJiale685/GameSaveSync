// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Data;
using GameSaveSync.Data.Repositories;
using GameSaveSync.IntegrationTests.Infrastructure;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 游戏运行会话跨设备同步：每会话一个不可变文件发布到共享仓库，
/// 各设备导入后按 SessionId 去重，总时长跨设备累加。
/// </summary>
public class PlaySessionSyncTests : MultiDeviceTestBase
{
    private static async Task InsertFinishedSessionAsync(
        string dbPath, string gameId, string startedAt, string endedAt)
    {
        await using var db = new SqliteDatabase(dbPath);
        await db.InitializeAsync();
        var repo = new PlaySessionRepository(db);
        var sessionId = Guid.NewGuid().ToString("D");
        await repo.InsertAsync(sessionId, gameId, startedAt);
        await repo.FinishAsync(sessionId, endedAt, PlaySessionEndReason.NormalExit);
    }

    [Fact]
    public async Task 两台设备的会话合并累加总时长()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        var appB = await CreateDeviceAppAsync(DbPathB);

        var config = await appA.CreateNewGameAsync("跨设备时长", SaveDirA);
        await appB.BindExistingGameAsync(config.GameId, "跨设备时长", SaveDirB);

        // 设备 A 记录 30 分钟会话
        await InsertFinishedSessionAsync(
            DbPathA, config.GameId, "2026-01-01T10:00:00Z", "2026-01-01T10:30:00Z");
        // 设备 B 记录 1 小时会话
        await InsertFinishedSessionAsync(
            DbPathB, config.GameId, "2026-01-02T10:00:00Z", "2026-01-02T11:00:00Z");

        // 模拟网盘双向同步：A 发布并拉取，B 发布并拉取，A 再拉取一次收敛。
        await appA.SyncPlaySessionsAsync(config.GameId, force: true);
        await appB.SyncPlaySessionsAsync(config.GameId, force: true);
        await appA.SyncPlaySessionsAsync(config.GameId, force: true);

        var statsA = await appA.GetGamePlayStatsAsync(config.GameId);
        var statsB = await appB.GetGamePlayStatsAsync(config.GameId);

        Assert.Equal(2, statsA.SessionCount);
        Assert.Equal(2, statsB.SessionCount);
        Assert.Equal(90 * 60, statsA.TotalSeconds);
        Assert.Equal(90 * 60, statsB.TotalSeconds);
        Assert.Equal("2026-01-01T10:00:00Z", statsA.FirstPlayedAt);
        Assert.Equal("2026-01-02T11:00:00Z", statsA.LastPlayedAt);
    }

    [Fact]
    public async Task 重复同步不会重复计数()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        var appB = await CreateDeviceAppAsync(DbPathB);

        var config = await appA.CreateNewGameAsync("去重", SaveDirA);
        await appB.BindExistingGameAsync(config.GameId, "去重", SaveDirB);

        await InsertFinishedSessionAsync(
            DbPathA, config.GameId, "2026-02-01T10:00:00Z", "2026-02-01T10:20:00Z");

        // 多次强制同步（A 发布 → B 导入 → B 再发布自己的会话 → A 导入）
        for (var i = 0; i < 3; i++)
        {
            await appA.SyncPlaySessionsAsync(config.GameId, force: true);
            await appB.SyncPlaySessionsAsync(config.GameId, force: true);
        }

        var statsA = await appA.GetGamePlayStatsAsync(config.GameId);
        var statsB = await appB.GetGamePlayStatsAsync(config.GameId);

        Assert.Equal(1, statsA.SessionCount);
        Assert.Equal(1, statsB.SessionCount);
        Assert.Equal(20 * 60, statsA.TotalSeconds);
        Assert.Equal(20 * 60, statsB.TotalSeconds);
    }
}
