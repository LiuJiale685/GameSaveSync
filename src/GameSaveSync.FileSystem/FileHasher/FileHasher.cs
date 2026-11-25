// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Security.Cryptography;
using GameSaveSync.Core.Services;

namespace GameSaveSync.FileSystem;

/// <summary>
/// 文件 SHA256 计算器（内容寻址与完整性校验的基础）。
/// </summary>
public static class FileHasher
{
    /// <summary>流式计算文件 SHA256（小写十六进制）。</summary>
    public static async Task<string> ComputeSha256Async(
        string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = SHA256.Create();
        var hash = await hasher.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
