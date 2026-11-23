using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Storage;

namespace GameSaveSync.Snapshot;

/// <summary>
/// Object 存储管理（方案 3.4）：按内容 SHA256 存取文件对象，文件级去重。
/// </summary>
public sealed class ObjectStore
{
    private readonly IRepositoryStore _store;

    public ObjectStore(IRepositoryStore store)
    {
        _store = store;
    }

    /// <summary>对象是否已存在于仓库。</summary>
    public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default)
        => _store.ExistsAsync(RepoPaths.ObjectKey(sha256), cancellationToken);

    /// <summary>读取对象内容流。</summary>
    public Task<Stream> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
        => _store.OpenReadAsync(RepoPaths.ObjectKey(sha256), cancellationToken);

    /// <summary>获取对象大小。</summary>
    public Task<long?> GetSizeAsync(string sha256, CancellationToken cancellationToken = default)
        => _store.GetSizeAsync(RepoPaths.ObjectKey(sha256), cancellationToken);

    /// <summary>
    /// 确保对象存在（方案 3.4 步骤 1-5）：
    /// 已存在时校验大小后复用；不存在时从源文件写入（内容寻址 + 哈希校验 + 临时文件改名发布）。
    /// 返回该对象的 sha256。
    /// </summary>
    public async Task<string> EnsureObjectAsync(
        string sourceFilePath,
        string sha256,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        var key = RepoPaths.ObjectKey(sha256);
        var existingSize = await _store.GetSizeAsync(key, cancellationToken).ConfigureAwait(false);
        if (existingSize is long size)
        {
            if (size != expectedSize)
                throw new RepoIntegrityException(
                    $"Object {sha256} 大小不匹配：仓库 {size}，源文件 {expectedSize}。");
            return sha256;
        }

        await using (var source = new FileStream(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await _store.PutImmutableAsync(key, source, sha256, cancellationToken)
                .ConfigureAwait(false);
        }

        return sha256;
    }

    /// <summary>
    /// 校验对象完整性：先比大小，再（verifyHash=true 时）完整 SHA256 校验（方案 5.2 / T05）。
    /// </summary>
    public async Task<ObjectVerifyResult> VerifyObjectAsync(
        string sha256,
        long expectedSize,
        bool verifyHash,
        CancellationToken cancellationToken = default)
    {
        var key = RepoPaths.ObjectKey(sha256);
        if (!await _store.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            return ObjectVerifyResult.Missing;

        var size = await _store.GetSizeAsync(key, cancellationToken).ConfigureAwait(false);
        if (size != expectedSize)
            return ObjectVerifyResult.SizeMismatch;

        if (!verifyHash)
            return ObjectVerifyResult.Ok;

        await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        var actual = await FolderRepositoryStore.ComputeSha256Async(stream, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(actual, sha256, StringComparison.Ordinal)
            ? ObjectVerifyResult.Ok
            : ObjectVerifyResult.HashMismatch;
    }

    /// <summary>统计 objects 目录的对象数量与总体积（方案 13.3 的 UI 统计需求）。</summary>
    public async Task<(int Count, long TotalBytes)> GetStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = await _store.ListAsync(RepoPaths.ObjectsPrefix, cancellationToken)
            .ConfigureAwait(false);
        long total = 0;
        foreach (var key in keys)
        {
            var size = await _store.GetSizeAsync(key, cancellationToken).ConfigureAwait(false);
            total += size ?? 0;
        }
        return (keys.Count, total);
    }
}

public enum ObjectVerifyResult
{
    Ok,
    Missing,
    SizeMismatch,
    HashMismatch,
}
