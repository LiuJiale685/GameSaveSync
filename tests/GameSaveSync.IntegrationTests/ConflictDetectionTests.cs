using System.Text;
using GameSaveSync.Core.Models;
using GameSaveSync.IntegrationTests.Infrastructure;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 冲突检测：T06（并行提交多 Head）、T07（本地未提交修改不被覆盖）。
/// </summary>
public class ConflictDetectionTests : MultiDeviceTestBase
{
    [Fact]
    public async Task T06_两设备并行修改_检测多Head冲突_两侧版本都保留在仓库()
    {
        // 共同基线：A 创建并备份，B 应用
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "shared.dat", "baseline-content");
        await WriteSaveFileAsync(SaveDirA, "a-only.dat", "a-only-baseline");
        var configA = await appA.CreateNewGameAsync("并行游戏", SaveDirA);
        var c1 = await appA.BackupNowAsync(configA.GameId, "共同基线");

        var appB = await CreateDeviceAppAsync(DbPathB);
        var configB = await appB.BindExistingGameAsync(configA.GameId, "并行游戏", SaveDirB);
        await appB.ApplyRemoteVersionAsync(configB.GameId, c1.CommitId);

        // A、B 各自基于基线修改同一文件并备份 → 分叉
        await WriteSaveFileAsync(SaveDirA, "shared.dat", "device-A-parallel-modification");
        var c2a = await appA.BackupNowAsync(configA.GameId, "A 的修改");

        await WriteSaveFileAsync(SaveDirB, "shared.dat", "device-B-different-modification");
        var c2b = await appB.BackupNowAsync(configB.GameId, "B 的修改");

        // B 扫描：两个并行 Head → 冲突，不自动选择任何一侧
        var scan = await appB.ScanGameAsync(configB.GameId);
        Assert.Equal(SyncStatus.ConflictDetected, scan.Status);
        Assert.Equal(2, scan.ConflictHeads.Count);
        Assert.Contains(scan.ConflictHeads, h => h.CommitId == c2a.CommitId);
        Assert.Contains(scan.ConflictHeads, h => h.CommitId == c2b.CommitId);
        Assert.Null(scan.AutoApplyCandidate); // 安全：绝不自动应用

        // 两侧 Commit 都在仓库版本历史中，均可恢复
        var versions = await appB.ListVersionsAsync(configB.GameId);
        Assert.Contains(versions, v => v.CommitId == c1.CommitId);
        Assert.Contains(versions, v => v.CommitId == c2a.CommitId);
        Assert.Contains(versions, v => v.CommitId == c2b.CommitId);

        // B 记录了待处理冲突
        var conflicts = await appB.ListConflictsAsync();
        Assert.Contains(conflicts, c => c.GameId == configB.GameId);

        // 本地数据未被覆盖
        Assert.Equal("device-B-different-modification",
            Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirB, "shared.dat")));
    }

    [Fact]
    public async Task T07_本地未提交修改遇远端新版本_冲突且不覆盖本地()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "shared.dat", "baseline-content");
        var configA = await appA.CreateNewGameAsync("游戏U", SaveDirA);
        var c1 = await appA.BackupNowAsync(configA.GameId, "基线");

        // B 应用基线
        var appB = await CreateDeviceAppAsync(DbPathB);
        var configB = await appB.BindExistingGameAsync(configA.GameId, "游戏U", SaveDirB);
        await appB.ApplyRemoteVersionAsync(configB.GameId, c1.CommitId);

        // B 本地修改（未备份提交），A 推送新版本
        await WriteSaveFileAsync(SaveDirB, "shared.dat", "B-local-uncommitted-edit-longer");
        await WriteSaveFileAsync(SaveDirA, "shared.dat", "A-new-version-remote");
        var c2 = await appA.BackupNowAsync(configA.GameId, "A 新版本");

        var scan = await appB.ScanGameAsync(configB.GameId);
        Assert.Equal(SyncStatus.ConflictDetected, scan.Status);
        Assert.Null(scan.AutoApplyCandidate); // 安全：本地脏，不自动应用
        var conflictHead = Assert.Single(scan.ConflictHeads);
        Assert.Equal(c2.CommitId, conflictHead.CommitId);

        // B 的本地修改完好保留，未被远端版本覆盖
        Assert.Equal("B-local-uncommitted-edit-longer",
            Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirB, "shared.dat")));
    }
}
