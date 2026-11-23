using System.Security.Cryptography;
using GameSaveSync.Core.Services;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Storage;

/// <summary>
/// 本地文件夹仓库适配器（方案 9.2）：把仓库 key 映射为
/// 用户配置的百度网盘双向同步目录下的物理路径。
/// 负责安全读写、哈希校验、目录列举、路径规范化和冲突副本容忍（3.4.1 / T18）。
/// </summary>
public sealed class FolderRepositoryStore : IRepositoryStore
{
    private readonly string _rootPath;

    public FolderRepositoryStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("仓库根目录不能为空。", nameof(rootPath));
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    /// <summary>key → 物理路径；校验不越界（T16）。</summary>
    public string ResolveKeyPath(string key)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_rootPath, relative));
        if (!PathNormalizer.IsUnderRoot(full, _rootPath))
            throw new ArgumentException($"仓库 key 越界: {key}");
        return full;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolveKeyPath(key);
        return Task.FromResult(File.Exists(path));
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolveKeyPath(key);
        Stream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public async Task PutImmutableAsync(
        string key, Stream content, string sha256, CancellationToken cancellationToken)
    {
        if (!RepoProtocol.IsValidSha256Hex(sha256))
            throw new ArgumentException($"写入仓库内容必须提供合法的小写 sha256: {sha256}");

        var targetPath = ResolveKeyPath(key);
        var targetDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDir);

        if (File.Exists(targetPath))
        {
            // create-if-absent：已存在则校验哈希一致即可复用，不覆盖。
            await VerifyFileSha256Async(targetPath, sha256, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 先写临时文件，写完校验哈希，再改名发布（方案 5.1 / 13.4）。
        var tempPath = Path.Combine(
            targetDir,
            $".{Guid.NewGuid():N}.tmp");

        try
        {
            var actualHash = await WriteAndHashAsync(content, tempPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actualHash, sha256, StringComparison.Ordinal))
                throw new RepoIntegrityException(
                    $"写入 {key} 时内容哈希不匹配：期望 {sha256}，实际 {actualHash}。");

            try
            {
                File.Move(tempPath, targetPath);
            }
            catch (IOException)
            {
                // 目标在此期间已被其他写入者创建（create-if-absent 竞态）：
                // 校验其内容一致则复用，不一致才视为仓库损坏。
                if (File.Exists(targetPath))
                    await VerifyFileSha256Async(targetPath, sha256, cancellationToken)
                        .ConfigureAwait(false);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var normalizedPrefix = prefix.TrimEnd('/');
        var prefixDir = ResolveKeyPath(normalizedPrefix);
        if (!Directory.Exists(prefixDir))
            return Task.FromResult<IReadOnlyList<string>>(result);

        var prefixWithSlash = prefix.TrimEnd('/') + "/";

        foreach (var file in Directory.EnumerateFiles(prefixDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var relative = prefixWithSlash + Path.GetRelativePath(prefixDir, file)
                .Replace(Path.DirectorySeparatorChar, '/');

            // 只保留符合规范命名的文件；网盘冲突副本（如 "xxx (1)"）等忽略（T18）。
            if (IsCanonicalName(relative, fileName))
                result.Add(relative);
        }

        result.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolveKeyPath(key);
        if (!File.Exists(path))
            return Task.FromResult<long?>(null);
        return Task.FromResult<long?>(new FileInfo(path).Length);
    }

    public Task<bool> TryDeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolveKeyPath(key);
        if (!File.Exists(path))
            return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>按 key 所在目录判断文件命名是否规范。</summary>
    private static bool IsCanonicalName(string relativeKey, string fileName)
    {
        if (relativeKey.StartsWith(RepoPaths.DevicesPrefix, StringComparison.Ordinal))
        {
            // devices/<uuid>.json
            var name = Path.GetFileNameWithoutExtension(fileName);
            return Guid.TryParseExact(name, "D", out _);
        }
        if (relativeKey.Contains("/manifests/", StringComparison.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            return RepoProtocol.IsValidSha256Hex(name);
        }
        if (relativeKey.Contains("/commits/", StringComparison.Ordinal))
        {
            var segments = relativeKey.Split('/');
            // games/<gid>/commits/<device8>/<uuid>.json
            return segments.Length == 5
                && RepoPaths.IsValidDeviceShortId(segments[3])
                && RepoPaths.TryGetCommitIdFromFileName(fileName) is not null;
        }
        if (relativeKey.StartsWith(RepoPaths.ObjectsPrefix, StringComparison.Ordinal))
        {
            return RepoPaths.TryGetObjectSha256FromFileName(fileName) is not null;
        }
        if (relativeKey.Contains("/game.json", StringComparison.Ordinal))
        {
            return fileName.Equals("game.json", StringComparison.OrdinalIgnoreCase);
        }
        return fileName.Equals("repo.json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifyFileSha256Async(
        string path, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new RepoIntegrityException(
                $"仓库文件 {path} 内容哈希不匹配：期望 {expectedSha256}，实际 {actual}。");
    }

    private static async Task<string> WriteAndHashAsync(
        Stream content, string targetPath, CancellationToken cancellationToken)
    {
        await using (var output = File.Create(targetPath))
        {
            await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        await using var readStream = File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ComputeSha256Async(readStream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var hasher = SHA256.Create();
        var hash = await hasher.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
