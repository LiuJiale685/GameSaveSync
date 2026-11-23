using System.Text;

namespace GameSaveSync.Core.Services;

/// <summary>
/// 存档相对路径规范化与安全校验（方案 6.5 / T16）。
/// 统一分隔符 '/'；禁止绝对路径、盘符、'..' 穿越、非法字符；
/// 保留原始大小写（Windows 不区分大小写，比较时用 OrdinalIgnoreCase）。
/// </summary>
public static class PathNormalizer
{
    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Where(c => c != '/' && c != '\\' && c != ':').ToArray();

    /// <summary>
    /// 将用户/系统提供的相对路径规范化为仓库规范路径。非法时抛出。
    /// </summary>
    public static string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("存档相对路径不能为空。", nameof(relativePath));

        var normalized = relativePath.Trim().Replace('\\', '/');

        if (normalized.StartsWith('/') || normalized.StartsWith("//"))
            throw new ArgumentException($"存档路径不允许是绝对路径: {relativePath}");
        if (normalized.Length >= 2 && normalized[1] == ':')
            throw new ArgumentException($"存档路径不允许包含盘符: {relativePath}");

        var segments = new List<string>();
        foreach (var raw in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Trim();
            if (seg.Length == 0 || seg == ".")
                continue;
            if (seg == "..")
                throw new ArgumentException($"存档路径不允许包含 '..' 路径穿越: {relativePath}");
            if (seg.IndexOfAny(InvalidChars) >= 0)
                throw new ArgumentException($"存档路径包含非法字符: {relativePath}");
            if (seg.EndsWith(" ") || seg.EndsWith("."))
                seg = seg.TrimEnd(' ', '.');
            if (seg.Length == 0)
                continue;
            segments.Add(seg);
        }

        if (segments.Count == 0)
            throw new ArgumentException($"存档路径规范化后为空: {relativePath}");

        return string.Join('/', segments);
    }

    /// <summary>尝试规范化，失败返回 null。</summary>
    public static string? TryNormalize(string relativePath)
    {
        try
        {
            return Normalize(relativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>判断一个绝对路径是否位于根目录之下（不跟随符号链接语义由扫描器保证）。</summary>
    public static bool IsUnderRoot(string fullPath, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static string ToRepoRelative(string normalizedPath) => normalizedPath;

    /// <summary>比较两个规范化路径是否相等（Windows 大小写不敏感）。</summary>
    public static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Manifest 文件排序与去重（保证稳定 Manifest）。</summary>
public static class ManifestOrdering
{
    public static IComparer<string> PathComparer { get; } = StringComparer.Ordinal;

    public static IEnumerable<T> OrderByPath<T>(IEnumerable<T> entries, Func<T, string> pathSelector)
        => entries.OrderBy(pathSelector, PathComparer);
}
