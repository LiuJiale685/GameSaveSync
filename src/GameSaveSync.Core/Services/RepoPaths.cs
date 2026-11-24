using System.Text.RegularExpressions;

namespace GameSaveSync.Core.Services;

/// <summary>
/// 仓库内部逻辑路径（key）规范，对应方案 3.1 的目录结构。
/// 所有仓库读写都通过 key 进行，由存储适配层映射为物理路径。
/// </summary>
public static partial class RepoPaths
{
    public const string RepoInfoKey = "repo.json";

    [GeneratedRegex("^[0-9a-f]{8}$")]
    private static partial Regex DeviceShortIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    public static string DeviceRegistrationKey(string deviceId)
        => $"devices/{NormalizeGuid(deviceId)}.json";

    public static string GameInfoKey(string gameId)
        => $"games/{NormalizeGuid(gameId)}/game.json";

    /// <summary>共享游戏设置（可覆盖更新，与不可变的 game.json 区分）。</summary>
    public static string GameSettingsKey(string gameId)
        => $"games/{NormalizeGuid(gameId)}/settings.json";

    public static string ManifestKey(string gameId, string manifestHash)
        => $"games/{NormalizeGuid(gameId)}/manifests/{manifestHash}.json";

    /// <summary>设备子目录使用 UUID 前 8 位短 ID 缓解路径长度（方案 3.5）。</summary>
    public static string CommitKey(string gameId, string deviceId, string commitId)
        => $"games/{NormalizeGuid(gameId)}/commits/{DeviceShortId(deviceId)}/{NormalizeGuid(commitId)}.json";

    public static string CommitsPrefix(string gameId)
        => $"games/{NormalizeGuid(gameId)}/commits/";

    public static string GamesPrefix => "games/";

    public static string DevicesPrefix => "devices/";

    public static string ObjectKey(string sha256)
        => $"objects/sha256/{sha256[..2]}/{sha256}";

    public static string ObjectsPrefix => "objects/sha256/";

    /// <summary>从 objects 目录下的文件名提取 sha256；非规范命名返回 null（网盘冲突副本容忍，T18）。</summary>
    public static string? TryGetObjectSha256FromFileName(string fileName)
        => Sha256Regex().IsMatch(fileName) ? fileName : null;

    /// <summary>从 commits 目录下的文件名提取 commitId；非规范命名返回 null。</summary>
    public static string? TryGetCommitIdFromFileName(string fileName)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = fileName[..^5];
        return Guid.TryParseExact(name, "D", out var g) && name == NormalizeGuid(g) ? name : null;
    }

    public static string DeviceShortId(string deviceId)
        => NormalizeGuid(deviceId)[..8];

    public static bool IsValidDeviceShortId(string value)
        => DeviceShortIdRegex().IsMatch(value);

    public static string NormalizeGuid(string value)
        => Guid.Parse(value).ToString("D");

    public static string NormalizeGuid(Guid value)
        => value.ToString("D");
}
