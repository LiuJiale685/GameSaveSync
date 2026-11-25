// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Text;
using GameSaveSync.Core.Models;
using GameSaveSync.IntegrationTests.Infrastructure;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 端到端双设备同步：T01（新设备接入与快进应用）、T17（绑定已有游戏复用共享 gameId）。
/// </summary>
public class EndToEndSyncTests : MultiDeviceTestBase
{
    [Fact]
    public async Task T01_新设备接入_确认后应用远端版本_目录与源设备逐文件一致()
    {
        // ---- 设备 A：创建游戏并备份首版本 ----
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "save1.dat", "save-data-A-version-1");
        await WriteSaveFileAsync(SaveDirA, "sub/save2.dat", "nested-save-B-version-1");
        var configA = await appA.CreateNewGameAsync("测试游戏", SaveDirA);
        var c1 = await appA.BackupNowAsync(configA.GameId, "首次备份");

        // ---- 设备 B：独立 db 绑定同一仓库，复用共享 gameId ----
        var appB = await CreateDeviceAppAsync(DbPathB);
        var configB = await appB.BindExistingGameAsync(configA.GameId, "测试游戏", SaveDirB);
        Assert.Equal(configA.GameId, configB.GameId); // T17：复用共享 gameId

        // B 无基线 → 冲突待确认，绝不自动应用（安全断言）
        var scanBeforeApply = await appB.ScanGameAsync(configB.GameId);
        Assert.Equal(SyncStatus.ConflictDetected, scanBeforeApply.Status);
        Assert.Null(scanBeforeApply.AutoApplyCandidate);
        Assert.Contains(scanBeforeApply.ConflictHeads, h => h.CommitId == c1.CommitId);
        Assert.Empty(ListRelativeFiles(SaveDirB)); // 未应用任何远端内容

        // ---- 用户确认后显式应用远端版本 ----
        await appB.ApplyRemoteVersionAsync(configB.GameId, c1.CommitId);
        AssertDirectoriesEqual(SaveDirA, SaveDirB, "应用首版本后");

        // ---- A 修改文件并发布新版本，B 扫描应得到自动应用候选 ----
        await WriteSaveFileAsync(SaveDirA, "save1.dat", "save-data-A-version-2-longer");
        var c2 = await appA.BackupNowAsync(configA.GameId, "修改后备份");

        var scanCandidate = await appB.ScanGameAsync(configB.GameId);
        Assert.Equal(SyncStatus.RemoteVersionVerified, scanCandidate.Status);
        Assert.NotNull(scanCandidate.AutoApplyCandidate);
        Assert.Equal(c2.CommitId, scanCandidate.AutoApplyCandidate!.CommitId);

        // ---- B 快进应用，目录与 A 逐文件字节一致 ----
        await appB.ApplyRemoteVersionAsync(configB.GameId, c2.CommitId);
        AssertDirectoriesEqual(SaveDirA, SaveDirB, "应用第二版本后");
    }

    [Fact]
    public async Task T17_绑定已有游戏_复用共享gameId_仓库游戏可列出()
    {
        // 设备 A 创建游戏（此时不需要备份）
        var appA = await CreateDeviceAppAsync(DbPathA);
        var configA = await appA.CreateNewGameAsync("共享游戏", SaveDirA);

        // 设备 B 列出仓库中的共享游戏并绑定
        var appB = await CreateDeviceAppAsync(DbPathB);
        var repoGames = await appB.ListRepoGamesAsync();
        var shared = Assert.Single(repoGames);
        Assert.Equal(configA.GameId, shared.GameId);
        Assert.Equal("共享游戏", shared.Name);

        // 绑定时复用共享 gameId，不新建 game.json
        var configB = await appB.BindExistingGameAsync(shared.GameId, "共享游戏", SaveDirB);
        Assert.Equal(configA.GameId, configB.GameId);
        Assert.Single(await appB.ListRepoGamesAsync());

        // B 本地游戏配置中可见（ListGamesAsync 不含设备路径，路径经 GetSavePath 查询）
        var localGames = await appB.ListGamesAsync();
        Assert.Contains(localGames, g => g.GameId == configA.GameId && g.Name == "共享游戏");
    }
}
