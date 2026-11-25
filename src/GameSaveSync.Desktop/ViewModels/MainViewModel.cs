// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel：聚合四个页签，联动刷新（游戏增删后其它页签更新游戏选择器）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public GamesViewModel Games { get; }

    public VersionsViewModel Versions { get; }

    public SyncStatusViewModel Sync { get; }

    public SettingsViewModel Settings { get; }

    public MainViewModel(
        GamesViewModel games,
        VersionsViewModel versions,
        SyncStatusViewModel sync,
        SettingsViewModel settings)
    {
        Games = games;
        Versions = versions;
        Sync = sync;
        Settings = settings;

        Games.GamesChanged += () =>
        {
            try
            {
                _ = Versions.LoadGamesCommand.ExecuteAsync(null);
                _ = Sync.LoadGamesCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "联动刷新游戏选择器失败。");
            }
        };
    }

    /// <summary>初始加载：游戏列表（联动其它页签）、设置、冲突列表。</summary>
    public async Task LoadInitialAsync()
    {
        await Games.LoadGamesCommand.ExecuteAsync(null);
        await Settings.LoadCommand.ExecuteAsync(null);
        await Sync.RefreshConflictsCommand.ExecuteAsync(null);
    }
}
