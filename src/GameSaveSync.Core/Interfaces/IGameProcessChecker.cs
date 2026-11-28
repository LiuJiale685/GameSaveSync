// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
namespace GameSaveSync.Core.Interfaces;

/// <summary>
/// 游戏进程检查（方案 6.2 / 7.3 / T08）。由 Desktop 层实现（UI 不能依赖方向）。
/// </summary>
public interface IGameProcessChecker
{
    /// <summary>
    /// 判断给定的进程名列表中是否有进程正在运行（名称不含 .exe 时也需匹配）。
    /// 提供 <paramref name="executablePath"/> 时应同时校验进程可执行文件位置，
    /// 以区分进程名相同但安装目录不同的多个游戏。
    /// </summary>
    Task<bool> IsAnyRunningAsync(
        IReadOnlyList<string> processNames,
        string? executablePath = null,
        CancellationToken cancellationToken = default);
}
