// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Text;
using GameSaveSync.Data;
using GameSaveSync.IntegrationTests.Infrastructure;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 仓库迁移：更换同步目录时把全部历史复制到新目录并切换绑定（T21）。
/// 断言重点：repositoryId 保持不变、历史版本可在新目录恢复、串仓被拒绝、旧目录按选择清理。
/// </summary>
public class RepositoryMigrationTests : MultiDeviceTestBase
{
    private string NewRepoDir => Path.Combine(Root, "repo-new");

    [Fact]
    public async Task T21_迁移到空目录_历史保留_新目录中可恢复旧版本()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "save1.dat", "v1-AAAA");
        await WriteSaveFileAsync(SaveDirA, "save2.dat", "v1-BBBB");
        var config = await appA.CreateNewGameAsync("迁移游戏", SaveDirA);
        var c1 = await appA.BackupNowAsync(config.GameId, "v1");

        await WriteSaveFileAsync(SaveDirA, "save1.dat", "v2-CCCCCCCC");
        var c2 = await appA.BackupNowAsync(config.GameId, "v2");

        var repoInfoBefore = await new GameSaveSync.Storage.RepoFileService(
            new GameSaveSync.Storage.FolderRepositoryStore(RepoDir)).TryReadRepoInfoAsync();

        var result = await appA.MigrateRepositoryAsync(NewRepoDir);

        // 迁移结果：路径切换、repositoryId 保持不变、旧目录保留
        Assert.Equal(Path.GetFullPath(NewRepoDir), result.NewRepoPath);
        Assert.Equal(repoInfoBefore!.RepositoryId, result.RepositoryId);
        Assert.False(result.SourceCleared);
        Assert.True(result.CopiedFileCount > 0);
        Assert.Equal(Path.GetFullPath(NewRepoDir), appA.RepoPath);

        // 新旧目录内容逐文件一致（完整历史已复制）
        AssertDirectoriesEqual(RepoDir, NewRepoDir, "迁移后仓库内容");

        // 迁移后仍能读到完整版本历史
        var versions = await appA.ListVersionsAsync(config.GameId);
        Assert.Equal(2, versions.Count);

        // 另一台设备绑定新目录，可以恢复旧版本
        var appB = new GameSaveSyncAppService(new SqliteDatabase(DbPathB), new NeverRunningProcessChecker());
        await using (appB)
        {
            await appB.InitializeAsync(NewRepoDir);
            await appB.BindExistingGameAsync(config.GameId, "迁移游戏", SaveDirB);

            var restore = await appB.RestoreVersionAsync(config.GameId, c1.CommitId);
            Assert.True(restore.Success);
            Assert.Equal("v1-AAAA", Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirB, "save1.dat")));
            Assert.Equal("v1-BBBB", Encoding.UTF8.GetString(await ReadSaveFileAsync(SaveDirB, "save2.dat")));
        }

        // 新目录中同样能看到最新版本（c2 是最初设备的提交）
        Assert.Contains(await new GameSaveSync.Storage.RepoFileService(
            new GameSaveSync.Storage.FolderRepositoryStore(NewRepoDir)).ListCommitsAsync(config.GameId),
            c => c.CommitId == c2.CommitId);
    }

    [Fact]
    public async Task T21_迁移时选择清理旧目录_旧目录被删除()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "only.dat", "content");
        var config = await appA.CreateNewGameAsync("清理测试", SaveDirA);
        await appA.BackupNowAsync(config.GameId, "v1");

        var result = await appA.MigrateRepositoryAsync(NewRepoDir, clearSourceRepository: true);

        Assert.True(result.SourceCleared);
        Assert.Null(result.CleanupError);
        Assert.False(Directory.Exists(RepoDir));
        Assert.True(File.Exists(Path.Combine(NewRepoDir, "repo.json")));
        Assert.Equal(Path.GetFullPath(NewRepoDir), appA.RepoPath);
    }

    [Fact]
    public async Task T21_目标目录是另一个仓库_拒绝迁移且不破坏两个仓库()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        await WriteSaveFileAsync(SaveDirA, "only.dat", "source-content");
        var config = await appA.CreateNewGameAsync("源仓库游戏", SaveDirA);
        await appA.BackupNowAsync(config.GameId, "v1");

        // 另一个独立仓库（不同 repositoryId）
        var otherRepoFiles = new GameSaveSync.Storage.RepoFileService(
            new GameSaveSync.Storage.FolderRepositoryStore(NewRepoDir));
        var otherRepo = await otherRepoFiles.InitializeRepoAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => appA.MigrateRepositoryAsync(NewRepoDir));
        Assert.Contains("另一个仓库", ex.Message);

        // 源仓库绑定未变，目标仓库仍只有自己的 repo.json
        Assert.Equal(Path.GetFullPath(RepoDir), appA.RepoPath);
        var stillOther = await otherRepoFiles.TryReadRepoInfoAsync();
        Assert.Equal(otherRepo.RepositoryId, stillOther!.RepositoryId);
        Assert.Empty(await otherRepoFiles.ListCommitsAsync(config.GameId));
        Assert.Empty(await otherRepoFiles.ListGamesAsync());
    }
}
