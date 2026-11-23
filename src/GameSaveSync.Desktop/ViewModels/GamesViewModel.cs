using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Core.Models;
using GameSaveSync.Desktop.Services;
using GameSaveSync.Sync;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// 游戏页：本机游戏列表、添加游戏、从仓库绑定已有游戏（T17）、删除游戏。
/// </summary>
public sealed partial class GamesViewModel : ViewModelBase
{
    private readonly GameSaveSyncAppService _appService;
    private readonly GameProcessMonitorService _monitor;

    public ObservableCollection<GameRowViewModel> Games { get; } = new();

    /// <summary>游戏增删后触发（供其它页签联动刷新游戏选择器、进程监控）。</summary>
    public event Action? GamesChanged;

    public GamesViewModel(
        GameSaveSyncAppService appService,
        GameProcessMonitorService monitor,
        IDialogService dialog)
        : base(dialog)
    {
        _appService = appService;
        _monitor = monitor;
    }

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        IReadOnlyList<LocalGameConfig> games = Array.Empty<LocalGameConfig>();
        var ok = await RunBusyAsync(
            async () => games = await _appService.ListGamesAsync(),
            "正在加载游戏列表…");
        if (!ok)
            return;

        Games.Clear();
        foreach (var game in games)
            Games.Add(new GameRowViewModel(game));

        // 进程监控使用最新配置（游戏退出自动备份）
        _monitor.UpdateGames(games);
        GamesChanged?.Invoke();
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var input = Dialog.ShowAddGameDialog(bindFromRepo: false, Array.Empty<SharedGameInfo>());
        if (input is null)
            return;

        var ok = await RunBusyAsync(
            () => _appService.CreateNewGameAsync(input.Name, input.SavePath, input.ProcessNames),
            $"正在添加游戏「{input.Name}」…");
        if (ok)
        {
            Dialog.Info($"游戏「{input.Name}」已添加。");
            await LoadGamesAsync();
        }
    }

    [RelayCommand]
    private async Task BindFromRepoAsync()
    {
        IReadOnlyList<SharedGameInfo> repoGames = Array.Empty<SharedGameInfo>();
        var ok = await RunBusyAsync(
            async () => repoGames = await _appService.ListRepoGamesAsync(),
            "正在读取仓库共享游戏…");
        if (!ok)
            return;

        if (repoGames.Count == 0)
        {
            Dialog.Info("仓库中暂无共享游戏。可先在本机“添加游戏”创建，之后其他设备即可绑定。");
            return;
        }

        var input = Dialog.ShowAddGameDialog(bindFromRepo: true, repoGames);
        if (input is null)
            return;

        ok = await RunBusyAsync(
            () => _appService.BindExistingGameAsync(input.SharedGameId, input.Name, input.SavePath, input.ProcessNames),
            $"正在绑定游戏「{input.Name}」…");
        if (ok)
        {
            Dialog.Info($"游戏「{input.Name}」已从仓库绑定（复用共享 gameId）。");
            await LoadGamesAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteGameAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;
        if (!Dialog.Confirm(
                $"确定删除游戏「{row.Name}」吗？\n仅移除本机配置，不影响共享仓库中的数据。",
                "删除游戏"))
            return;

        var ok = await RunBusyAsync(
            () => _appService.RemoveGameAsync(row.GameId),
            $"正在删除游戏「{row.Name}」…");
        if (ok)
        {
            Dialog.Info($"游戏「{row.Name}」已删除。");
            await LoadGamesAsync();
        }
    }
}
