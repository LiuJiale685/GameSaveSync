namespace GameSaveSync.Core.Models;

/// <summary>
/// 设备本地的游戏配置（保存于本机 SQLite，绝不写入共享仓库，见方案 6.2/3.1.1）。
/// </summary>
public sealed class LocalGameConfig
{
    public string GameId { get; set; } = string.Empty;

    /// <summary>显示名称（可来自共享 game.json 或本机命名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本机存档目录（设备本地，各设备可不同）。</summary>
    public string SavePath { get; set; } = string.Empty;

    /// <summary>可选进程名列表，用于判断游戏是否运行。</summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>游戏可执行文件完整路径（设备本地配置，用于启动游戏；空串表示未配置）。</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    public bool AutoBackup { get; set; } = true;

    public bool AutoRestore { get; set; } = true;
}
