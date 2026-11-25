// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Security.Cryptography;
using System.Text;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Storage;
using Xunit;

namespace GameSaveSync.UnitTests;

/// <summary>
/// T19：仓库协议 schema 版本校验 —— 不匹配的仓库文件必须被拒绝。
/// </summary>
public class RepoSchemaTests : IDisposable
{
    private readonly string _root;

    public RepoSchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gss-schema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<string> WriteRawFileAsync(
        FolderRepositoryStore store, string key, string json)
    {
        // 绕过元数据服务，直接向仓库写入手工构造的 JSON（用真实 sha 以通过 PutImmutable 校验）。
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var stream = new MemoryStream(bytes, writable: false);
        await store.PutImmutableAsync(key, stream, sha, CancellationToken.None);
        return sha;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void EnsureSupportedSchemaVersion_版本不匹配_抛RepoSchemaException(int badVersion)
    {
        Assert.Throws<RepoSchemaException>(
            () => RepoProtocol.EnsureSupportedSchemaVersion(badVersion, "测试上下文"));
    }

    [Fact]
    public void EnsureSupportedSchemaVersion_当前版本_不抛()
    {
        RepoProtocol.EnsureSupportedSchemaVersion(RepoProtocol.SchemaVersion, "测试上下文");
    }

    [Fact]
    public async Task TryReadRepoInfoAsync_repoJson版本不匹配_抛RepoSchemaException()
    {
        var store = new FolderRepositoryStore(_root);
        await WriteRawFileAsync(store, RepoPaths.RepoInfoKey, """
            {
              "schemaVersion": 99,
              "repositoryId": "0b6f3e2a-9a9a-4c2e-9f01-0000000000ff",
              "createdAtUtc": "2026-01-01T00:00:00Z",
              "application": "GameSaveSync"
            }
            """);
        var repoFiles = new RepoFileService(store);

        await Assert.ThrowsAsync<RepoSchemaException>(() => repoFiles.TryReadRepoInfoAsync());
    }

    [Fact]
    public async Task TryReadManifestAsync_manifest版本不匹配_抛RepoSchemaException()
    {
        var store = new FolderRepositoryStore(_root);
        var repoFiles = new RepoFileService(store);
        var gameId = "0b6f3e2a-9a9a-4c2e-9f01-000000000001";
        var json = """
            {
              "schemaVersion": 99,
              "gameId": "0b6f3e2a-9a9a-4c2e-9f01-000000000001",
              "files": []
            }
            """;

        // Manifest 内容寻址：key 中的哈希必须是内容的真实 SHA256。
        var bytes = Encoding.UTF8.GetBytes(json);
        var manifestHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var stream = new MemoryStream(bytes, writable: false);
        await store.PutImmutableAsync(
            RepoPaths.ManifestKey(gameId, manifestHash), stream, manifestHash, CancellationToken.None);

        await Assert.ThrowsAsync<RepoSchemaException>(
            () => repoFiles.TryReadManifestAsync(gameId, manifestHash));
    }

    [Fact]
    public async Task ListCommitsAsync_commit版本不匹配_抛RepoSchemaException()
    {
        var store = new FolderRepositoryStore(_root);
        var gameId = "0b6f3e2a-9a9a-4c2e-9f01-000000000001";
        var deviceId = "0b6f3e2a-9a9a-4c2e-9f01-000000000002";
        var commitId = "0b6f3e2a-9a9a-4c2e-9f01-000000000003";
        await WriteRawFileAsync(store, RepoPaths.CommitKey(gameId, deviceId, commitId), $$"""
            {
              "schemaVersion": 99,
              "commitId": "{{commitId}}",
              "gameId": "{{gameId}}",
              "deviceId": "{{deviceId}}",
              "parentIds": [],
              "manifestHash": "{{new string('a', 64)}}",
              "createdAtUtc": "2026-01-01T00:00:00Z",
              "message": null
            }
            """);
        var repoFiles = new RepoFileService(store);

        await Assert.ThrowsAsync<RepoSchemaException>(() => repoFiles.ListCommitsAsync(gameId));
    }

    [Fact]
    public async Task TryReadManifestAsync_正常版本_可读回并校验哈希()
    {
        var store = new FolderRepositoryStore(_root);
        var repoFiles = new RepoFileService(store);
        var gameId = "0b6f3e2a-9a9a-4c2e-9f01-000000000001";
        var manifest = new Manifest
        {
            GameId = gameId,
            Files =
            [
                new ManifestFileEntry
                {
                    Path = "save.dat",
                    Sha256 = new string('b', 64),
                    Size = 10,
                    LastWriteUtc = "2026-01-01T00:00:00Z",
                },
            ],
        };

        var manifestHash = await repoFiles.WriteManifestAsync(gameId, manifest);
        var restored = await repoFiles.TryReadManifestAsync(gameId, manifestHash);

        Assert.NotNull(restored);
        Assert.Equal(gameId, restored!.GameId);
        var entry = Assert.Single(restored.Files);
        Assert.Equal("save.dat", entry.Path);
    }
}
