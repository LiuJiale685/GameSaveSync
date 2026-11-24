using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Storage;

/// <summary>
/// 仓库元数据读写服务：repo.json、devices、games、manifests、commits。
/// 全部通过 IRepositoryStore 进行，写入均为 create-if-absent / 不可变语义。
/// </summary>
public sealed class RepoFileService
{
    private readonly IRepositoryStore _store;

    public RepoFileService(IRepositoryStore store)
    {
        _store = store;
    }

    /// <summary>初始化仓库：repo.json 不存在则创建；存在则读取并校验 schema（T19）。</summary>
    public async Task<RepoInfo> InitializeRepoAsync(CancellationToken cancellationToken = default)
    {
        var existing = await TryReadRepoInfoAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var repo = new RepoInfo
        {
            RepositoryId = Guid.NewGuid().ToString("D"),
            CreatedAtUtc = UtcClock.Now(),
        };
        await WriteImmutableJsonAsync(
            RepoPaths.RepoInfoKey, repo, cancellationToken).ConfigureAwait(false);
        return repo;
    }

    public async Task<RepoInfo?> TryReadRepoInfoAsync(CancellationToken cancellationToken = default)
    {
        if (!await _store.ExistsAsync(RepoPaths.RepoInfoKey, cancellationToken).ConfigureAwait(false))
            return null;

        await using var stream = await _store.OpenReadAsync(RepoPaths.RepoInfoKey, cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var repo = RepoJson.DeserializeOrThrow<RepoInfo>(json, RepoPaths.RepoInfoKey);
        RepoProtocol.EnsureSupportedSchemaVersion(repo.SchemaVersion, RepoPaths.RepoInfoKey);
        return repo;
    }

    public async Task RegisterDeviceAsync(DeviceRegistration device, CancellationToken cancellationToken = default)
    {
        await WriteImmutableJsonAsync(
            RepoPaths.DeviceRegistrationKey(device.DeviceId), device, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeviceRegistration>> ListDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = await _store.ListAsync(RepoPaths.DevicesPrefix, cancellationToken).ConfigureAwait(false);
        var result = new List<DeviceRegistration>();
        foreach (var key in keys)
        {
            await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var device = RepoJson.Deserialize<DeviceRegistration>(json);
            if (device is not null)
                result.Add(device);
        }
        return result;
    }

    /// <summary>
    /// 创建共享 game.json（create-if-absent，方案 3.1.1）：
    /// 已存在（包括其他设备先创建）时直接复用，不覆盖。
    /// </summary>
    public async Task<SharedGameInfo> EnsureGameAsync(
        string gameId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var key = RepoPaths.GameInfoKey(gameId);
        if (await _store.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
        {
            var existing = await ReadGameAsync(gameId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }

        var game = new SharedGameInfo
        {
            GameId = gameId,
            Name = name,
            Description = description,
            CreatedAtUtc = UtcClock.Now(),
        };
        await WriteImmutableJsonAsync(key, game, cancellationToken).ConfigureAwait(false);
        return game;
    }

    public async Task<SharedGameInfo?> ReadGameAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        var key = RepoPaths.GameInfoKey(gameId);
        if (!await _store.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            return null;
        await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var game = RepoJson.DeserializeOrThrow<SharedGameInfo>(json, key);
        RepoProtocol.EnsureSupportedSchemaVersion(game.SchemaVersion, key);
        return game;
    }

    /// <summary>扫描仓库中所有共享游戏（设备绑定已有游戏的前置，方案 4.3.1）。</summary>
    public async Task<IReadOnlyList<SharedGameInfo>> ListGamesAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = await _store.ListAsync("games", cancellationToken).ConfigureAwait(false);
        var result = new List<SharedGameInfo>();
        foreach (var key in keys.Where(k =>
                     k.EndsWith("/game.json", StringComparison.Ordinal)))
        {
            await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var game = RepoJson.Deserialize<SharedGameInfo>(json);
            if (game is not null)
                result.Add(game);
        }
        return result;
    }

    /// <summary>读取共享游戏设置（进程名等）；不存在返回 null。</summary>
    public async Task<SharedGameSettings?> ReadGameSettingsAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        var key = RepoPaths.GameSettingsKey(gameId);
        if (!await _store.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            return null;

        await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        // 设置文件允许跨版本共存：解析失败按"无设置"处理，不阻断同步。
        var settings = RepoJson.Deserialize<SharedGameSettings>(json);
        if (settings is null)
            return null;
        RepoProtocol.EnsureSupportedSchemaVersion(settings.SchemaVersion, key);
        return settings;
    }

    /// <summary>写入共享游戏设置（覆盖更新，后写者胜）。</summary>
    public async Task WriteGameSettingsAsync(
        SharedGameSettings settings, CancellationToken cancellationToken = default)
    {
        var bytes = RepoJson.SerializeUtf8(settings);
        await using var stream = new MemoryStream(bytes);
        await _store.PutMutableAsync(
            RepoPaths.GameSettingsKey(settings.GameId), stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入 Manifest，返回其内容 SHA256（manifestHash）。</summary>
    public async Task<string> WriteManifestAsync(
        string gameId, Manifest manifest, CancellationToken cancellationToken = default)
    {
        var bytes = RepoJson.SerializeUtf8(manifest);
        var manifestHash = await ComputeSha256Async(bytes, cancellationToken).ConfigureAwait(false);
        var key = RepoPaths.ManifestKey(gameId, manifestHash);
        await using var stream = new MemoryStream(bytes);
        await _store.PutImmutableAsync(key, stream, manifestHash, cancellationToken)
            .ConfigureAwait(false);
        return manifestHash;
    }

    public async Task<Manifest?> TryReadManifestAsync(
        string gameId, string manifestHash, CancellationToken cancellationToken = default)
    {
        var key = RepoPaths.ManifestKey(gameId, manifestHash);
        if (!await _store.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            return null;

        await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var manifest = RepoJson.DeserializeOrThrow<Manifest>(json, key);
        RepoProtocol.EnsureSupportedSchemaVersion(manifest.SchemaVersion, key);

        // 校验 Manifest 自身哈希，防止内容被篡改或截断。
        var bytes = RepoJson.SerializeUtf8(manifest);
        var actualHash = await ComputeSha256Async(bytes, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifestHash, StringComparison.Ordinal))
            throw new RepoIntegrityException(
                $"Manifest {manifestHash}（game {gameId}）自身哈希校验失败。");

        return manifest;
    }

    public async Task PublishCommitAsync(
        CommitInfo commit, CancellationToken cancellationToken = default)
    {
        var bytes = RepoJson.SerializeUtf8(commit);
        var key = RepoPaths.CommitKey(commit.GameId, commit.DeviceId, commit.CommitId);
        await using var stream = new MemoryStream(bytes);
        // Commit 文件名即身份，无需内容哈希寻址；这里用 commitId 派生值占位不可行，
        // 因此 PutImmutableAsync 对非内容寻址 key 允许任意 sha256——此处传入文件自身哈希以保证校验一致。
        var fileHash = await ComputeSha256Async(bytes, cancellationToken).ConfigureAwait(false);
        await _store.PutImmutableAsync(key, stream, fileHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>扫描某游戏的全部 Commit（跨设备，方案 3.6）。</summary>
    public async Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        var keys = await _store
            .ListAsync(RepoPaths.CommitsPrefix(gameId), cancellationToken)
            .ConfigureAwait(false);
        var result = new List<CommitInfo>();
        foreach (var key in keys)
        {
            await using var stream = await _store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var commit = RepoJson.Deserialize<CommitInfo>(json);
            if (commit is null)
                continue;
            RepoProtocol.EnsureSupportedSchemaVersion(commit.SchemaVersion, key);
            result.Add(commit);
        }
        return result;
    }

    /// <summary>读取某 Commit 引用的 Manifest（依赖不完整时返回 null）。</summary>
    public async Task<Manifest?> TryReadManifestByCommitAsync(
        string gameId, string commitId, CancellationToken cancellationToken = default)
    {
        var commits = await ListCommitsAsync(gameId, cancellationToken).ConfigureAwait(false);
        var commit = commits.FirstOrDefault(c =>
            string.Equals(c.CommitId, commitId, StringComparison.OrdinalIgnoreCase));
        if (commit is null)
            return null;
        return await TryReadManifestAsync(gameId, commit.ManifestHash, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteImmutableJsonAsync<T>(
        string key, T value, CancellationToken cancellationToken) where T : class
    {
        var bytes = RepoJson.SerializeUtf8(value);
        var hash = await ComputeSha256Async(bytes, cancellationToken).ConfigureAwait(false);
        await using var stream = new MemoryStream(bytes);
        await _store.PutImmutableAsync(key, stream, hash, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes);
        return await FolderRepositoryStore.ComputeSha256Async(stream, cancellationToken)
            .ConfigureAwait(false);
    }
}
