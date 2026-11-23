using GameSaveSync.Core.Models;
using GameSaveSync.Data.Repositories;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>游戏列表行（名称、存档路径、进程名）。</summary>
public sealed class GameRowViewModel
{
    public LocalGameConfig Config { get; }

    public string GameId => Config.GameId;

    public string Name => Config.Name;

    public string SavePath => Config.SavePath;

    public string ProcessNamesText => Config.ProcessNames.Count == 0
        ? "（未配置）"
        : string.Join(", ", Config.ProcessNames);

    public GameRowViewModel(LocalGameConfig config) => Config = config;
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
