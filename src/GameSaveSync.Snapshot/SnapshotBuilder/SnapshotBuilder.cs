// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.FileSystem;
using GameSaveSync.Storage;

namespace GameSaveSync.Snapshot;

/// <summary>
/// 快照构建器（方案 6.3 / 6.4 / 5.1）：
/// 从游戏存档目录生成稳定快照 — 文件稳定性检查、SHA256、
/// Object 写入、Manifest 构建，并校验 Manifest 引用的 Object 完整存在。
/// </summary>
public sealed class SnapshotBuilder
{
    private readonly FileScanner _scanner = new();
    private readonly ObjectStore _objectStore;

    /// <summary>文件稳定性检查的间隔（方案 6.4 建议 2~3 秒）。</summary>
    public TimeSpan StabilityCheckDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>被占用/持续变化文件的最大重试次数。</summary>
    public int MaxRetriesPerFile { get; set; } = 3;

    public SnapshotBuilder(IRepositoryStore store)
    {
        _objectStore = new ObjectStore(store);
    }

    /// <summary>
    /// 快照构建结果。
    /// </summary>
    public sealed class SnapshotResult
    {
        public Manifest Manifest { get; init; } = null!;

        /// <summary>Manifest 内容的 SHA256。</summary>
        public string ManifestHash { get; init; } = string.Empty;

        /// <summary>本次新写入的对象数（去重后）。</summary>
        public int NewObjectCount { get; init; }

        /// <summary>复用的已有对象数。</summary>
        public int ReusedObjectCount { get; init; }
    }

    /// <summary>
    /// 构建快照（不含 Commit 发布；Commit 由 CommitManager 负责）。
    /// </summary>
    public async Task<SnapshotResult> BuildAsync(
        string savePath,
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var scanned = _scanner.Scan(savePath, cancellationToken);

        var entries = new List<ManifestFileEntry>(scanned.Count);
        int newObjects = 0, reusedObjects = 0;

        foreach (var file in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 有限次重试复制（方案 6.4 第 6/7 步）。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var stable = await EnsureFileStableAsync(file.FullPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!stable)
                        throw new IOException($"文件持续变化或不可读: {file.RelativePath}");

                    var sha256 = await FileHasher.ComputeSha256Async(file.FullPath, cancellationToken)
                        .ConfigureAwait(false);

                    var before = await _objectStore
                        .ExistsAsync(sha256, cancellationToken)
                        .ConfigureAwait(false);

                    await _objectStore
                        .EnsureObjectAsync(file.FullPath, sha256, file.Size, cancellationToken)
                        .ConfigureAwait(false);

                    if (before)
                        reusedObjects++;
                    else
                        newObjects++;

                    entries.Add(new ManifestFileEntry
                    {
                        Path = file.RelativePath,
                        Sha256 = sha256,
                        Size = file.Size,
                        LastWriteUtc = UtcClock.FormatUtc(file.LastWriteUtc),
                    });
                    break;
                }
                catch (IOException) when (attempt < MaxRetriesPerFile)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var manifest = new Manifest
        {
            GameId = gameId,
            Files = ManifestOrdering.OrderByPath(entries, e => e.Path).ToList(),
        };

        var manifestBytes = RepoJson.SerializeUtf8(manifest);
        await using (var hashStream = new MemoryStream(manifestBytes))
        {
            var manifestHash = await FolderRepositoryStore
                .ComputeSha256Async(hashStream, cancellationToken)
                .ConfigureAwait(false);

            // 校验 Manifest 引用的全部 Object 在本地仓库中完整存在（方案 5.1 步骤 5）。
            await VerifyManifestObjectsAsync(manifest, verifyHash: false, cancellationToken)
                .ConfigureAwait(false);

            return new SnapshotResult
            {
                Manifest = manifest,
                ManifestHash = manifestHash,
                NewObjectCount = newObjects,
                ReusedObjectCount = reusedObjects,
            };
        }
    }

    /// <summary>
    /// 校验 Manifest 引用的全部 Object 完整存在（可选完整哈希校验）。
    /// 返回缺失或校验失败的对象列表（方案 5.2 / T05）。
    /// </summary>
    public async Task<IReadOnlyList<string>> FindBrokenObjectsAsync(
        Manifest manifest,
        bool verifyHash,
        CancellationToken cancellationToken = default)
    {
        var broken = new List<string>();
        foreach (var entry in manifest.Files)
        {
            var result = await _objectStore
                .VerifyObjectAsync(entry.Sha256, entry.Size, verifyHash, cancellationToken)
                .ConfigureAwait(false);
            if (result != ObjectVerifyResult.Ok)
                broken.Add(entry.Sha256);
        }
        return broken;
    }

    /// <summary>校验失败时抛出 RepoIntegrityException。</summary>
    public async Task VerifyManifestObjectsAsync(
        Manifest manifest, bool verifyHash, CancellationToken cancellationToken = default)
    {
        var broken = await FindBrokenObjectsAsync(manifest, verifyHash, cancellationToken)
            .ConfigureAwait(false);
        if (broken.Count > 0)
        {
            throw new RepoIntegrityException(
                $"Manifest 引用的 {broken.Count} 个 Object 缺失或校验失败: {string.Join(", ", broken.Take(5))}…");
        }
    }

    /// <summary>
    /// 文件稳定性检查（方案 6.4）：两次读取大小/修改时间一致才算稳定。
    /// </summary>
    private async Task<bool> EnsureFileStableAsync(
        string fullPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetriesPerFile; attempt++)
        {
            (long size, DateTime write)? first;
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                    return false;
                first = (info.Length, info.LastWriteTimeUtc);
            }
            catch (IOException)
            {
                await Task.Delay(StabilityCheckDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(StabilityCheckDelay, cancellationToken).ConfigureAwait(false);

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                    return false;
                if (info.Length == first.Value.size && info.LastWriteTimeUtc == first.Value.write)
                    return true;
            }
            catch (IOException)
            {
                // 继续下一轮
            }
        }
        return false;
    }
}
