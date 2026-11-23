using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.FileSystem;
using GameSaveSync.Snapshot;
using GameSaveSync.Storage;
using Serilog;

namespace GameSaveSync.Sync.CommitManager;

/// <summary>
/// Commit 管理（方案 5.1 发布顺序）：
/// 快照 → Object → Manifest → 校验依赖完整 → 最后发布 Commit → 更新本地状态。
/// </summary>
public sealed class CommitManager
{
    private readonly IRepositoryStore _store;
    private readonly RepoFileService _repoFiles;
    private readonly SnapshotBuilder _snapshotBuilder;

    public CommitManager(
        IRepositoryStore store,
        RepoFileService repoFiles,
        SnapshotBuilder snapshotBuilder)
    {
        _store = store;
        _repoFiles = repoFiles;
        _snapshotBuilder = snapshotBuilder;
    }

    /// <summary>
    /// 为当前游戏目录创建新 Commit（方案 5.1）。
    /// parentIds 由调用方决定（正常提交 = 基线；冲突收敛 = 多个父版本）。
    /// </summary>
    public async Task<CommitInfo> CreateCommitAsync(
        string gameId,
        string deviceId,
        string savePath,
        IReadOnlyList<string> parentIds,
        string message,
        CancellationToken cancellationToken = default)
    {
        // 1-2. 稳定快照 + SHA256 + 3. 写入缺失 Object（SnapshotBuilder 内部完成）
        var snapshot = await _snapshotBuilder
            .BuildAsync(savePath, gameId, cancellationToken)
            .ConfigureAwait(false);

        // 4. 写入 Manifest
        var manifestHash = await _repoFiles
            .WriteManifestAsync(gameId, snapshot.Manifest, cancellationToken)
            .ConfigureAwait(false);

        // 5. 校验 Manifest 引用的 Object 完整存在（BuildAsync 已做；此处复核）
        await _snapshotBuilder
            .VerifyManifestObjectsAsync(snapshot.Manifest, verifyHash: false, cancellationToken)
            .ConfigureAwait(false);

        // 6. 最后发布 Commit（不可变、UUID 命名）
        var commit = new CommitInfo
        {
            CommitId = Guid.NewGuid().ToString("D"),
            GameId = gameId,
            DeviceId = deviceId,
            ParentIds = parentIds.ToList(),
            ManifestHash = manifestHash,
            CreatedAtUtc = UtcClock.Now(),
            Message = message,
        };
        await _repoFiles.PublishCommitAsync(commit, cancellationToken).ConfigureAwait(false);

        Log.Information(
            "已发布 Commit {CommitId}（game {GameId}，新对象 {New}，复用 {Reused}）",
            commit.CommitId, gameId, snapshot.NewObjectCount, snapshot.ReusedObjectCount);

        return commit;
    }

    /// <summary>
    /// 创建指向已有 Manifest 的恢复/收敛 Commit（方案 8.3：回滚也是新版本）。
    /// 不重新构建快照，直接复用目标 Manifest。
    /// </summary>
    public async Task<CommitInfo> CreateCommitReferencingExistingManifestAsync(
        string gameId,
        string deviceId,
        string manifestHash,
        IReadOnlyList<string> parentIds,
        string message,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _repoFiles.TryReadManifestAsync(gameId, manifestHash, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RepoIntegrityException($"Manifest {manifestHash} 不存在，无法创建引用它的 Commit。");

        await _snapshotBuilder
            .VerifyManifestObjectsAsync(manifest, verifyHash: false, cancellationToken)
            .ConfigureAwait(false);

        var commit = new CommitInfo
        {
            CommitId = Guid.NewGuid().ToString("D"),
            GameId = gameId,
            DeviceId = deviceId,
            ParentIds = parentIds.ToList(),
            ManifestHash = manifestHash,
            CreatedAtUtc = UtcClock.Now(),
            Message = message,
        };
        await _repoFiles.PublishCommitAsync(commit, cancellationToken).ConfigureAwait(false);
        return commit;
    }
}
