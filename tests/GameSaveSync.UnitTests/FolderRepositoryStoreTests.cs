using System.Security.Cryptography;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Storage;
using Xunit;

namespace GameSaveSync.UnitTests;

public class FolderRepositoryStoreTests : IDisposable
{
    private readonly string _root;

    public FolderRepositoryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gss-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static MemoryStream StreamOf(byte[] content) => new(content, writable: false);

    [Fact]
    public async Task PutImmutableAsync_写入后可读回相同内容()
    {
        var store = new FolderRepositoryStore(_root);
        var content = "hello world"u8.ToArray();
        var key = RepoPaths.ObjectKey(Sha256(content));

        await store.PutImmutableAsync(key, StreamOf(content), Sha256(content), CancellationToken.None);

        Assert.True(await store.ExistsAsync(key, CancellationToken.None));
        await using var stream = await store.OpenReadAsync(key, CancellationToken.None);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());
        Assert.Equal(content.Length, await store.GetSizeAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task PutImmutableAsync_相同key相同内容_重复写入不报错()
    {
        var store = new FolderRepositoryStore(_root);
        var content = "deterministic content"u8.ToArray();
        var key = RepoPaths.ObjectKey(Sha256(content));

        await store.PutImmutableAsync(key, StreamOf(content), Sha256(content), CancellationToken.None);
        // create-if-absent：重复写入相同内容必须幂等。
        await store.PutImmutableAsync(key, StreamOf(content), Sha256(content), CancellationToken.None);

        Assert.True(await store.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task PutImmutableAsync_相同key不同内容_抛RepoIntegrityException()
    {
        var store = new FolderRepositoryStore(_root);
        var content1 = "content-one"u8.ToArray();
        var content2 = "content-two-different"u8.ToArray();
        var key = RepoPaths.ObjectKey(Sha256(content1));

        await store.PutImmutableAsync(key, StreamOf(content1), Sha256(content1), CancellationToken.None);

        await Assert.ThrowsAsync<RepoIntegrityException>(() =>
            store.PutImmutableAsync(key, StreamOf(content2), Sha256(content2), CancellationToken.None));
    }

    [Fact]
    public async Task PutImmutableAsync_声明哈希与内容不符_抛RepoIntegrityException()
    {
        var store = new FolderRepositoryStore(_root);
        var content = "actual bytes"u8.ToArray();
        var declaredHash = Sha256("not the actual content"u8.ToArray());
        var key = RepoPaths.ObjectKey(Sha256(content));

        await Assert.ThrowsAsync<RepoIntegrityException>(() =>
            store.PutImmutableAsync(key, StreamOf(content), declaredHash, CancellationToken.None));

        Assert.False(await store.ExistsAsync(key, CancellationToken.None)); // 未发布
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // 大写不合法
    [InlineData("")]
    public async Task PutImmutableAsync_非法sha256_抛ArgumentException(string badSha)
    {
        var store = new FolderRepositoryStore(_root);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutImmutableAsync("objects/sha256/xx/whatever", StreamOf("x"u8.ToArray()), badSha,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("../../outside-repo")]
    [InlineData("objects/../../outside")]
    [InlineData("C:/windows/evil")]
    [InlineData("C:\\windows\\evil")]
    public void ResolveKeyPath_越界key_抛ArgumentException(string escapingKey)
    {
        var store = new FolderRepositoryStore(_root);

        Assert.Throws<ArgumentException>(() => store.ResolveKeyPath(escapingKey));
    }

    [Fact]
    public async Task ListAsync_objects目录_忽略网盘冲突副本等非规范命名文件()
    {
        // T18：网盘同步产生的 "xxx (1)" 冲突副本不得进入对象列表。
        var store = new FolderRepositoryStore(_root);
        var sha = new string('f', 64);
        var dir = Path.Combine(_root, "objects", "sha256", "ab");
        Directory.CreateDirectory(dir);
        var canonical = Path.Combine(dir, sha);
        await File.WriteAllTextAsync(canonical, "object");
        await File.WriteAllTextAsync(Path.Combine(dir, sha + " (1)"), "conflict copy"); // 冲突副本
        await File.WriteAllTextAsync(Path.Combine(dir, "not-a-sha.txt"), "garbage");
        await File.WriteAllTextAsync(Path.Combine(dir, sha[..8]), "truncated name");

        var keys = await store.ListAsync("objects/sha256", CancellationToken.None);

        var key = Assert.Single(keys);
        Assert.Equal($"objects/sha256/ab/{sha}", key);
    }

    [Fact]
    public async Task ListAsync_devices目录_忽略非GUID命名文件()
    {
        var store = new FolderRepositoryStore(_root);
        var dir = Path.Combine(_root, "devices");
        Directory.CreateDirectory(dir);
        var guid = "0b6f3e2a-9a9a-4c2e-9f01-000000000001";
        await File.WriteAllTextAsync(Path.Combine(dir, guid + ".json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dir, guid + " (1).json"), "{}"); // 冲突副本
        await File.WriteAllTextAsync(Path.Combine(dir, "laptop.json"), "{}");       // 非规范

        var keys = await store.ListAsync("devices", CancellationToken.None);

        var key = Assert.Single(keys);
        Assert.Equal($"devices/{guid}.json", key);
    }

    [Fact]
    public async Task ListAsync_commits目录_忽略非规范命名的Commit文件()
    {
        var store = new FolderRepositoryStore(_root);
        var gameId = "0b6f3e2a-9a9a-4c2e-9f01-0000000000aa";
        var commitId = "0b6f3e2a-9a9a-4c2e-9f01-0000000000bb";
        var dir = Path.Combine(_root, "games", gameId, "commits", "0b6f3e2a");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, commitId + ".json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dir, commitId + " (1).json"), "{}"); // 冲突副本
        await File.WriteAllTextAsync(Path.Combine(dir, "random-name.json"), "{}");      // 非规范

        var keys = await store.ListAsync(RepoPaths.CommitsPrefix(gameId), CancellationToken.None);

        var key = Assert.Single(keys);
        Assert.Equal($"games/{gameId}/commits/0b6f3e2a/{commitId}.json", key);
    }

    [Fact]
    public async Task TryDeleteAsync_存在时删除并返回true_不存在返回false()
    {
        var store = new FolderRepositoryStore(_root);
        var content = "to-be-deleted"u8.ToArray();
        var key = RepoPaths.ObjectKey(Sha256(content));
        await store.PutImmutableAsync(key, StreamOf(content), Sha256(content), CancellationToken.None);

        Assert.True(await store.TryDeleteAsync(key, CancellationToken.None));
        Assert.False(await store.TryDeleteAsync(key, CancellationToken.None));
        Assert.False(await store.ExistsAsync(key, CancellationToken.None));
    }
}
