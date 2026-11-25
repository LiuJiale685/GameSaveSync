// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.IntegrationTests.Infrastructure;
using GameSaveSync.Storage;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// T11：备份过程崩溃的半成品提交 —— 只发布了 Commit/Manifest 而未上传 Object 时，
/// 另一设备必须进入"等待文件"状态，绝不应用，也不写入本地目录。
/// </summary>
public class HalfCommitTests : MultiDeviceTestBase
{
    [Fact]
    public async Task T11_只发布Commit未上传Object_另一设备等待文件且不应用()
    {
        // 通过 RepoFileService 直接构造半成品：Manifest 引用了从未上传的 Object。
        var gameId = Guid.NewGuid().ToString("D");
        var store = new FolderRepositoryStore(RepoDir);
        var repoFiles = new RepoFileService(store);
        await repoFiles.InitializeRepoAsync();
        await repoFiles.EnsureGameAsync(gameId, "半成品游戏", null);

        var phantomSha = Sha256Hex("object-content-never-uploaded"u8.ToArray());
        var manifest = new Manifest
        {
            GameId = gameId,
            Files =
            [
                new ManifestFileEntry
                {
                    Path = "save.dat",
                    Sha256 = phantomSha,
                    Size = 27,
                    LastWriteUtc = null,
                },
            ],
        };
        var manifestHash = await repoFiles.WriteManifestAsync(gameId, manifest);
        var commit = new CommitInfo
        {
            CommitId = Guid.NewGuid().ToString("D"),
            GameId = gameId,
            DeviceId = Guid.NewGuid().ToString("D"),
            ParentIds = [],
            ManifestHash = manifestHash,
            CreatedAtUtc = UtcClock.Now(),
            Message = "备份中途中断的半成品提交",
        };
        await repoFiles.PublishCommitAsync(commit); // Object 未写入仓库

        // B 绑定该游戏后扫描 → 等待文件
        var appB = await CreateDeviceAppAsync(DbPathB);
        var configB = await appB.BindExistingGameAsync(gameId, "半成品游戏", SaveDirB);

        var scan = await appB.ScanGameAsync(configB.GameId);
        Assert.Equal(SyncStatus.WaitingForFiles, scan.Status);
        Assert.Null(scan.AutoApplyCandidate); // 安全：不自动应用
        Assert.Contains(scan.WaitingCommits, c => c.CommitId == commit.CommitId);

        // 显式应用同样被完整性校验拒绝
        await Assert.ThrowsAsync<RepoIntegrityException>(
            () => appB.ApplyRemoteVersionAsync(configB.GameId, commit.CommitId));

        // 本地目录未被写入任何文件
        Assert.Empty(ListRelativeFiles(SaveDirB));
    }
}
