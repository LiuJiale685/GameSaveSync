namespace GameSaveSync.Core.Models;

/// <summary>
/// 仓库协议常量与校验。
/// </summary>
public static class RepoProtocol
{
    /// <summary>本机支持的仓库协议版本（对应 repo.json 的 schemaVersion）。</summary>
    public const int SchemaVersion = 1;

    public const string ApplicationName = "GameSaveSync";

    /// <summary>SHA256 十六进制小写长度。</summary>
    public const int Sha256HexLength = 64;

    /// <summary>校验 manifest/commit 中引用的哈希格式。</summary>
    public static bool IsValidSha256Hex(string value)
        => !string.IsNullOrEmpty(value)
           && value.Length == Sha256HexLength
           && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>校验 schemaVersion；不匹配时抛出（T19）。</summary>
    public static void EnsureSupportedSchemaVersion(int schemaVersion, string context)
    {
        if (schemaVersion != SchemaVersion)
        {
            throw new RepoSchemaException(
                $"仓库文件 {context} 的 schemaVersion={schemaVersion} 与本机支持的版本 {SchemaVersion} 不匹配，已拒绝操作。");
        }
    }
}

/// <summary>仓库 schema 版本不匹配异常（T19）。</summary>
public sealed class RepoSchemaException : Exception
{
    public RepoSchemaException(string message) : base(message) { }
}

/// <summary>仓库完整性/依赖缺失异常。</summary>
public sealed class RepoIntegrityException : Exception
{
    public RepoIntegrityException(string message) : base(message) { }
}
