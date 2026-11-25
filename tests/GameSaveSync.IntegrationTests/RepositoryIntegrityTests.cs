// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Text;
using GameSaveSync.Core.Models;
using GameSaveSync.IntegrationTests.Infrastructure;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 仓库完整性：T04（Object 缺失）、T05（Object 内容破坏）。
/// 核心安全断言：依赖不完整时绝不应用、绝不覆盖本地用户数据。
/// </summary>
public class RepositoryIntegrityTests : MultiDeviceTestBase
{
    [Fact]
    public async Task T04_Object缺失_扫描进入等待文件_应用被拒绝且本地不被触碰()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        var original = "important-data-v1"u8.ToArray();
        await WriteSaveFileAsync(SaveDirA, "only.dat", original);
        var config = await appA.CreateNewGameAsync("游戏W", SaveDirA);
        var c1 = await appA.BackupNowAsync(config.GameId, "v1");

        var manifest = await appA.GetManifestAsync(config.GameId, c1.ManifestHash);
        var sha = Assert.Single(manifest!.Files).Sha256;

        // 模拟网盘同步不完整：手动从仓库删除 object 文件
        var objectPath = PhysicalObjectPath(RepoDir, sha);
        Assert.True(File.Exists(objectPath));
        File.Delete(objectPath);

        var scan = await appA.ScanGameAsync(config.GameId);
        Assert.Equal(SyncStatus.WaitingForFiles, scan.Status);
        Assert.Null(scan.AutoApplyCandidate); // 安全：不提供自动应用候选
        Assert.Contains(scan.WaitingCommits, c => c.CommitId == c1.CommitId);

        // 应用被完整性校验拒绝
        await Assert.ThrowsAsync<RepoIntegrityException>(
            () => appA.ApplyRemoteVersionAsync(config.GameId, c1.CommitId));

        // 本地存档未被触碰
        Assert.Equal(original, await ReadSaveFileAsync(SaveDirA, "only.dat"));
    }

    [Fact]
    public async Task T05_Object内容被破坏_不自动应用_恢复抛RepoIntegrityException()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        var original = "important-data-v1"u8.ToArray();
        await WriteSaveFileAsync(SaveDirA, "only.dat", original);
        var config = await appA.CreateNewGameAsync("游戏V", SaveDirA);
        var c1 = await appA.BackupNowAsync(config.GameId, "v1");

        var manifest = await appA.GetManifestAsync(config.GameId, c1.ManifestHash);
        var sha = Assert.Single(manifest!.Files).Sha256;
        var objectPath = PhysicalObjectPath(RepoDir, sha);

        // ---- 场景 1：等长破坏（大小校验通过，须靠完整哈希校验兜底）----
        var sameSizeCorruption = new byte[original.Length];
        Array.Fill(sameSizeCorruption, (byte)0xAA);
        Assert.NotEqual(original, sameSizeCorruption);
        await File.WriteAllBytesAsync(objectPath, sameSizeCorruption);

        var scanSameSize = await appA.ScanGameAsync(config.GameId);
        Assert.Null(scanSameSize.AutoApplyCandidate); // 本地已是该版本，无自动应用
        // 完整恢复链路必须通过哈希校验发现破坏并拒绝
        await Assert.ThrowsAsync<RepoIntegrityException>(
            () => appA.ApplyRemoteVersionAsync(config.GameId, c1.CommitId));
        Assert.Equal(original, await ReadSaveFileAsync(SaveDirA, "only.dat")); // 未覆盖

        // ---- 场景 2：变长破坏 → 扫描直接进入 WaitingForFiles ----
        await File.WriteAllBytesAsync(objectPath, "short-corruption"u8.ToArray());
        var scanSizeMismatch = await appA.ScanGameAsync(config.GameId);
        Assert.Equal(SyncStatus.WaitingForFiles, scanSizeMismatch.Status);
        Assert.Null(scanSizeMismatch.AutoApplyCandidate);
        Assert.Contains(scanSizeMismatch.WaitingCommits, c => c.CommitId == c1.CommitId);

        await Assert.ThrowsAsync<RepoIntegrityException>(
            () => appA.ApplyRemoteVersionAsync(config.GameId, c1.CommitId));
        Assert.Equal(original, await ReadSaveFileAsync(SaveDirA, "only.dat")); // 未覆盖
    }
}
