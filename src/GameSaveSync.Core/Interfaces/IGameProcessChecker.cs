namespace GameSaveSync.Core.Interfaces;

/// <summary>
/// 游戏进程检查（方案 6.2 / 7.3 / T08）。由 Desktop 层实现（UI 不能依赖方向）。
/// </summary>
public interface IGameProcessChecker
{
    /// <summary>判断给定的进程名列表中是否有进程正在运行（名称不含 .exe 时也需匹配）。</summary>
    Task<bool> IsAnyRunningAsync(IReadOnlyList<string> processNames, CancellationToken cancellationToken = default);
}
