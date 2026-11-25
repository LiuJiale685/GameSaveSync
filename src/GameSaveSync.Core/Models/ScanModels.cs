// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
namespace GameSaveSync.Core.Models;

/// <summary>
/// 扫描得到的存档文件条目（相对路径 + 元数据）。
/// </summary>
public sealed class ScannedFile
{
    /// <summary>规范化相对路径（'/' 分隔，禁止绝对路径和路径穿越）。</summary>
    public string RelativePath { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset LastWriteUtc { get; init; }
}

/// <summary>
/// 本地游戏目录状态：与某个 Manifest 比较用于判断“本地是否有未提交修改”。
/// </summary>
public enum LocalDirectoryState
{
    /// <summary>目录内容与基线 Manifest 一致。</summary>
    Clean = 0,

    /// <summary>目录内容与基线 Manifest 不一致（有未提交修改）。</summary>
    Dirty = 1,

    /// <summary>基线不明确（从未应用过任何版本）。</summary>
    UnknownBaseline = 2,

    /// <summary>目录不存在或为空。
    /// 语义上按“与基线不一致”处理，但 UI 可据此给出更准确的提示。</summary>
    EmptyOrMissing = 3,
}
