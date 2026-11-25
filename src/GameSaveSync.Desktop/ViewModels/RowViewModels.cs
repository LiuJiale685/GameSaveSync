// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using CommunityToolkit.Mvvm.ComponentModel;
using GameSaveSync.Core.Models;
using GameSaveSync.Data.Repositories;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>游戏列表行（名称、存档路径、进程名、运行状态）。</summary>
public sealed partial class GameRowViewModel : ObservableObject
{
    public LocalGameConfig Config { get; }

    public string GameId => Config.GameId;

    public string Name => Config.Name;

    public string SavePath => Config.SavePath;

    public string ProcessNamesText => Config.ProcessNames.Count == 0
        ? "（未配置）"
        : string.Join(", ", Config.ProcessNames);

    /// <summary>是否已配置可执行文件（决定“启动游戏”是否可用）。</summary>
    public bool CanLaunch => !string.IsNullOrWhiteSpace(Config.ExecutablePath);

    public string ExecutablePathText => CanLaunch ? Config.ExecutablePath : "（未配置）";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(LaunchActionText))]
    [NotifyPropertyChangedFor(nameof(LaunchIconText))]
    [NotifyPropertyChangedFor(nameof(CanToggleLaunch))]
    private bool _isRunning;

    public string StatusText => IsRunning ? "运行中" : "已停止";

    /// <summary>可停止：正在运行且配置了进程名。</summary>
    public bool CanStop => IsRunning && Config.ProcessNames.Count > 0;

    /// <summary>可删除 / 可编辑：游戏未运行（运行中修改或删除配置有风险）。</summary>
    public bool CanDelete => !IsRunning;

    /// <summary>已隐藏的游戏在“显示已隐藏”时以灰色名称提示。</summary>
    public bool IsHidden => Config.IsHidden;

    public string HideActionText => IsHidden ? "取消隐藏" : "隐藏";

    /// <summary>启动 / 停止按钮文案：未运行时为“启动”，运行中变为“停止”。</summary>
    public string LaunchActionText => IsRunning ? "停止" : "启动";

    /// <summary>启动 / 停止按钮图标字符：未运行为 ▶，运行中为 ■。</summary>
    public string LaunchIconText => IsRunning ? "■" : "▶";

    /// <summary>启动/停止按钮可用性：未运行时需配置 exe，运行中需配置进程名。</summary>
    public bool CanToggleLaunch => IsRunning ? CanStop : CanLaunch;

    /// <summary>列表行的悬停提示：完整配置（存档目录、进程名、游戏程序、GameId）。</summary>
    public string DetailText => string.Join(Environment.NewLine,
        Name,
        $"存档目录：{SavePath}",
        $"进程名：{ProcessNamesText}",
        $"游戏程序：{ExecutablePathText}",
        $"GameId：{GameId}");

    public GameRowViewModel(LocalGameConfig config, bool isRunning = false)
    {
        Config = config;
        _isRunning = isRunning;
    }
}

/// <summary>游戏下拉选择项（版本历史页 / 同步状态页共用）。</summary>
public sealed class GameOptionViewModel
{
    public string GameId { get; }

    public string DisplayName { get; }

    public GameOptionViewModel(LocalGameConfig config)
    {
        GameId = config.GameId;
        DisplayName = config.Name;
    }
}

/// <summary>版本历史行（时间、设备、说明、commitId 前 8 位、父版本数）。</summary>
public sealed class VersionRowViewModel
{
    public CommitInfo Commit { get; }

    public string TimeDisplay => FormatUtil.FormatUtcTimestamp(Commit.CreatedAtUtc);

    public string DeviceDisplay => FormatUtil.ShortId(Commit.DeviceId);

    public string Message => Commit.Message ?? string.Empty;

    public string ShortId => FormatUtil.ShortId(Commit.CommitId);

    public string ParentCountText => Commit.ParentIds.Count.ToString();

    public VersionRowViewModel(CommitInfo commit) => Commit = commit;
}

/// <summary>冲突列表行。</summary>
public sealed class ConflictRowViewModel
{
    public ConflictRecord Record { get; }

    public string GameDisplay => FormatUtil.ShortId(Record.GameId);

    public string CreatedDisplay => FormatUtil.FormatUtcTimestamp(Record.CreatedAt);

    public string LocalCommitDisplay => FormatUtil.ShortId(Record.LocalCommitId);

    public string RemoteCommitsDisplay => Record.RemoteCommitIds.Count == 0
        ? "—"
        : string.Join(", ", Record.RemoteCommitIds.Select(id => FormatUtil.ShortId(id)));

    public ConflictRowViewModel(ConflictRecord record) => Record = record;
}
