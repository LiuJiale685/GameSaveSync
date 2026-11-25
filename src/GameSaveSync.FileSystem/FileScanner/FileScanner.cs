// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;

namespace GameSaveSync.FileSystem;

/// <summary>
/// 存档目录扫描器（方案 2.2 / 6.5）：
/// 递归扫描存档目录，产出规范相对路径文件列表。
/// - 不跟随符号链接 / Reparse Point（T16）。
/// - 拒绝越界与路径穿越。
/// - 大小写与分隔符规范化。
/// </summary>
public sealed class FileScanner
{
    private readonly EnumerationOptions _enumerationOptions;

    public FileScanner()
    {
        _enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // 不跟随符号链接/junction
            ReturnSpecialDirectories = false,
        };
    }

    /// <summary>
    /// 扫描存档目录。目录不存在时返回空列表。
    /// </summary>
    public IReadOnlyList<ScannedFile> Scan(string savePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(savePath);
        if (!Directory.Exists(root))
            return Array.Empty<ScannedFile>();

        var result = new List<ScannedFile>();
        foreach (var fullPath in Directory.EnumerateFiles(root, "*", _enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 双重保险：即便 AttributesToSkip 漏掉，最终路径必须仍在根下。
            if (!PathNormalizer.IsUnderRoot(fullPath, root))
                continue;

            var relative = Path.GetRelativePath(root, fullPath);
            string normalized;
            try
            {
                normalized = PathNormalizer.Normalize(relative);
            }
            catch (ArgumentException)
            {
                continue; // 非规范命名文件不进入清单
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists)
                    continue;
            }
            catch (IOException)
            {
                continue;
            }

            result.Add(new ScannedFile
            {
                RelativePath = normalized,
                FullPath = info.FullName,
                Size = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            });
        }

        return ManifestOrdering.OrderByPath(result, f => f.RelativePath).ToList();
    }
}
