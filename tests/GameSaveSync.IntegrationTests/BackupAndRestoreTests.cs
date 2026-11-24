using System.Text;
using GameSaveSync.Core.Models;
using GameSaveSync.IntegrationTests.Infrastructure;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 备份与恢复：T02（增量对象）、T03（删除文件后的备份与恢复）、T10（恢复的版本历史语义）。
/// </summary>
public class BackupAndRestoreTests : MultiDeviceTestBase
{
    [Fact]
    public async Task T02_修改单文件再备份_仓库只新增对应新对象()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "save1.dat", "v1-content-AAAA");
        await WriteSaveFileAsync(SaveDirA, "save2.dat", "v2-content-BBBB");
        var config = await appA.CreateNewGameAsync("游戏X", SaveDirA);
        var c1 = await appA.BackupNowAsync(config.GameId, "首版");

        var (countAfterFirst, totalBytesAfterFirst) = await appA.GetRepoStatsAsync();
        Assert.Equal(2, countAfterFirst); // 两个文件 → 两个对象
        Assert.Equal(CountPhysicalRepoObjects(RepoDir), countAfterFirst);

        // 仅修改 save1.dat（内容与大小都变化）
        await WriteSaveFileAsync(SaveDirA, "save1.dat", "v2-content-CCCCCCCCCCCC");
        var c2 = await appA.BackupNowAsync(config.GameId, "修改单文件");

        // 只新增 save1 的新对象；save2 复用旧对象
        var (countAfterSecond, _) = await appA.GetRepoStatsAsync();
        Assert.Equal(countAfterFirst + 1, countAfterSecond);
        Assert.Equal(CountPhysicalRepoObjects(RepoDir), countAfterSecond);

        // Manifest 层面：save2 哈希不变，save1 哈希变化
        var m1 = await appA.GetManifestAsync(config.GameId, c1.ManifestHash);
        var m2 = await appA.GetManifestAsync(config.GameId, c2.ManifestHash);
        Assert.NotNull(m1);
        Assert.NotNull(m2);
        Assert.Equal(
            m1!.Files.Single(f => f.Path == "save2.dat").Sha256,
            m2!.Files.Single(f => f.Path == "save2.dat").Sha256);
        Assert.NotEqual(
            m1.Files.Single(f => f.Path == "save1.dat").Sha256,
            m2.Files.Single(f => f.Path == "save1.dat").Sha256);

        // 未修改文件的对象内容未被覆盖（总字节数只增加新对象大小）
        var (_, totalBytesAfterSecond) = await appA.GetRepoStatsAsync();
        Assert.Equal(totalBytesAfterFirst + "v2-content-CCCCCCCCCCCC".Length, totalBytesAfterSecond);
    }

    [Fact]
    public async Task T03_删除文件后备份_新清单不含该文件_恢复旧版本文件回来()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "keep.dat", "keep-content");
        await WriteSaveFileAsync(SaveDirA, "drop.dat", "drop-content");
        var config = await appA.CreateNewGameAsync("游戏Y", SaveDirA);
        var c1 = await appA.BackupNowAsync(config.GameId, "初始");

        var m1 = await appA.GetManifestAsync(config.GameId, c1.ManifestHash);
        Assert.Contains(m1!.Files, f => f.Path == "drop.dat");

        // 删除文件后备份
        File.Delete(Path.Combine(SaveDirA, "drop.dat"));
        var c2 = await appA.BackupNowAsync(config.GameId, "删除文件后");

        var m2 = await appA.GetManifestAsync(config.GameId, c2.ManifestHash);
        Assert.DoesNotContain(m2!.Files, f => f.Path == "drop.dat"); // 新版本清单不含被删文件
        Assert.Contains(m2.Files, f => f.Path == "keep.dat");
        Assert.False(File.Exists(Path.Combine(SaveDirA, "drop.dat")));

        var versionsBeforeRestore = await appA.ListVersionsAsync(config.GameId);
        Assert.Equal(2, versionsBeforeRestore.Count);

        // 恢复旧版本：文件回来，且恢复前的当前状态被自动备份为版本
        var restore = await appA.RestoreVersionAsync(config.GameId, c1.CommitId);
        Assert.True(restore.Success);

        Assert.True(File.Exists(Path.Combine(SaveDirA, "drop.dat")));
        Assert.Equal("drop-content",
            Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirA, "drop.dat")));
        Assert.Equal("keep-content",
            Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirA, "keep.dat")));

        // 版本数 +2：恢复前自动备份 + 恢复 Commit
        var versionsAfterRestore = await appA.ListVersionsAsync(config.GameId);
        Assert.Equal(versionsBeforeRestore.Count + 2, versionsAfterRestore.Count);
        Assert.Contains(versionsAfterRestore, v => v.Message == "恢复前自动备份");
        Assert.Contains(versionsAfterRestore, v => v.Message == "恢复历史版本");
    }

    [Fact]
    public async Task T10_恢复旧版本_目录等于旧内容_历史新增备份与恢复两个Commit()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        const string v1Progress = "progress-version-1";
        await WriteSaveFileAsync(SaveDirA, "progress.dat", v1Progress);
        await WriteSaveFileAsync(SaveDirA, "settings.cfg", "settings-common");
        var config = await appA.CreateNewGameAsync("游戏Z", SaveDirA);
        var c1 = await appA.BackupNowAsync(config.GameId, "v1");

        const string v2Progress = "progress-version-2-with-more-data";
        await WriteSaveFileAsync(SaveDirA, "progress.dat", v2Progress);
        var c2 = await appA.BackupNowAsync(config.GameId, "v2");
        Assert.NotEqual(c1.ManifestHash, c2.ManifestHash);

        // 恢复到 v1
        var restore = await appA.RestoreVersionAsync(config.GameId, c1.CommitId);
        Assert.True(restore.Success);
        Assert.Equal(v1Progress,
            Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirA, "progress.dat")));

        // 版本历史：v1、v2、恢复前自动备份、恢复 Commit
        var versions = await appA.ListVersionsAsync(config.GameId);
        Assert.Equal(4, versions.Count);
        var backupCommit = versions.Single(v => v.Message == "恢复前自动备份");
        var restoreCommit = versions.Single(v => v.Message == "恢复历史版本");

        // 恢复 Commit 复用目标版本的 Manifest，父版本为恢复前备份 + 被恢复的目标版本
        // （同时引用目标版本，使目标版本不再是并行 Head）
        Assert.Equal(c1.ManifestHash, restoreCommit.ManifestHash);
        Assert.Equal(
            new[] { backupCommit.CommitId, c1.CommitId }.OrderBy(x => x, StringComparer.Ordinal),
            restoreCommit.ParentIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { c2.CommitId }, backupCommit.ParentIds);

        // 恢复 Commit 成为唯一 Head，AppliedVersions 指向它 → 扫描显示"已是最新"
        var scan = await appA.ScanGameAsync(config.GameId);
        Assert.Equal(SyncStatus.RemoteVersionVerified, scan.Status);
        Assert.Equal(restoreCommit.CommitId, scan.LastAppliedCommitId);
        Assert.Equal(restoreCommit.CommitId, Assert.Single(scan.Heads).CommitId);
        Assert.Null(scan.AutoApplyCandidate);
    }
}
