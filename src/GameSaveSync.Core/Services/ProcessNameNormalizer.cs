// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
namespace GameSaveSync.Core.Services;

/// <summary>
/// 进程名规范化：仓库与本地配置统一保存为带 .exe 后缀的格式（如 Game.exe）。
/// 比较时忽略大小写；Windows 的 Process.ProcessName 不带后缀，比较前需同样规范化。
/// </summary>
public static class ProcessNameNormalizer
{
    public const string ExeSuffix = ".exe";

    /// <summary>规范化为 "名称.exe"（去空白、去重复后缀、保留原始大小写）。</summary>
    public static string Normalize(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        // 去掉用户可能输入的多个 .exe 后缀（如 game.exe.exe）
        while (trimmed.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^ExeSuffix.Length].TrimEnd();

        return trimmed.Length == 0 ? string.Empty : trimmed + ExeSuffix;
    }

    /// <summary>批量规范化：去空、去重（忽略大小写），保持首次出现顺序。</summary>
    public static List<string> NormalizeAll(IEnumerable<string> names)
        => names
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>去掉 .exe 后缀，用于与 Process.ProcessName 比较（不含后缀）。</summary>
    public static string ToProcessName(string name)
    {
        var normalized = Normalize(name);
        return normalized.Length == 0 ? string.Empty : normalized[..^ExeSuffix.Length];
    }
}
