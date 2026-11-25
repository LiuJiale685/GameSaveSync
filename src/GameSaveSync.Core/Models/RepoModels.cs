// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Text.Json.Serialization;

namespace GameSaveSync.Core.Models;

/// <summary>
/// repo.json — 仓库唯一标识。创建即不可变。
/// </summary>
public sealed class RepoInfo
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = RepoProtocol.SchemaVersion;

    [JsonPropertyOrder(1)]
    public string RepositoryId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string Application { get; set; } = "GameSaveSync";
}

/// <summary>
/// devices/&lt;device-id&gt;.json — 设备注册信息（非敏感）。
/// </summary>
public sealed class DeviceRegistration
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = RepoProtocol.SchemaVersion;

    [JsonPropertyOrder(1)]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string CreatedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// games/&lt;game-id&gt;/game.json — 共享游戏元数据。
/// 只包含共享字段（见方案 3.1.1）；创建即不可变（create-if-absent）。
/// </summary>
public sealed class SharedGameInfo
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = RepoProtocol.SchemaVersion;

    [JsonPropertyOrder(1)]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(4)]
    public string? Description { get; set; }
}

/// <summary>
/// games/&lt;game-id&gt;/settings.json — 可跨设备同步的共享游戏设置（进程名等）。
/// 与不可变的 game.json 分开存放，允许覆盖更新（后写者胜）。
/// </summary>
public sealed class SharedGameSettings
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = RepoProtocol.SchemaVersion;

    [JsonPropertyOrder(1)]
    public string GameId { get; set; } = string.Empty;

    /// <summary>进程名列表，统一严格带 .exe 后缀（如 game.exe），跨设备共享。</summary>
    [JsonPropertyOrder(2)]
    public List<string> ProcessNames { get; set; } = new();

    [JsonPropertyOrder(3)]
    public string UpdatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(4)]
    public string UpdatedByDeviceId { get; set; } = string.Empty;
}

/// <summary>
/// Manifest 内的单个文件条目。
/// </summary>
public sealed class ManifestFileEntry
{
    [JsonPropertyOrder(0)]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public long Size { get; set; }

    [JsonPropertyOrder(3)]
    public string? LastWriteUtc { get; set; }
}

/// <summary>
/// games/&lt;game-id&gt;/manifests/&lt;manifest-hash&gt;.json — 版本文件清单。
/// 序列化必须确定性（固定属性顺序、固定缩进、UTF-8 无 BOM、Invariant Culture）。
/// </summary>
public sealed class Manifest
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = RepoProtocol.SchemaVersion;

    [JsonPropertyOrder(1)]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public List<ManifestFileEntry> Files { get; set; } = new();
}

/// <summary>
/// games/&lt;game-id&gt;/commits/&lt;device-short-id&gt;/&lt;commit-id&gt;.json — 不可变提交记录。
/// </summary>
public sealed class CommitInfo
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = RepoProtocol.SchemaVersion;

    [JsonPropertyOrder(1)]
    public string CommitId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyOrder(4)]
    public List<string> ParentIds { get; set; } = new();

    [JsonPropertyOrder(5)]
    public string ManifestHash { get; set; } = string.Empty;

    [JsonPropertyOrder(6)]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(7)]
    public string? Message { get; set; }
}
