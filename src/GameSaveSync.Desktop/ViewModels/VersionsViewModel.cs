using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Core.Models;
using GameSaveSync.Desktop.Services;
using GameSaveSync.Sync;
using ApplyResult = GameSaveSync.Sync.RestoreManager.RestoreManager.ApplyResult;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// 版本历史页：选游戏 → 版本列表、查看文件清单、恢复历史版本（自动先备份当前目录）。
/// </summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly GameSaveSyncAppService _appService;

    public ObservableCollection<GameOptionViewModel> Games { get; } = new();

    [ObservableProperty]
    private GameOptionViewModel? _selectedGame;

    public ObservableCollection<VersionRowViewModel> Versions { get; } = new();

    public VersionsViewModel(GameSaveSyncAppService appService, IDialogService dialog)
        : base(dialog)
    {
        _appService = appService;
    }

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        var previousId = SelectedGame?.GameId;
        IReadOnlyList<LocalGameConfig> games = Array.Empty<LocalGameConfig>();
        var ok = await RunBusyAsync(
            async () => games = await _appService.ListGamesAsync(),
            "正在加载游戏列表…");
        if (!ok)
            return;

        Games.Clear();
        foreach (var game in games)
            Games.Add(new GameOptionViewModel(game));

        var target = Games.FirstOrDefault(g => g.GameId == previousId) ?? Games.FirstOrDefault();
        if (ReferenceEquals(target, SelectedGame))
        {
            // 选择未变（含双双为空）：手动刷新版本列表
            await LoadVersionsAsync();
        }
        else
        {
            // 选择变化（含置空）：通过 OnSelectedGameChanged 自动加载
            SelectedGame = target;
        }
    }

    partial void OnSelectedGameChanged(GameOptionViewModel? value)
    {
        if (value is null)
        {
            Versions.Clear();
            return;
        }
        if (IsBusy)
            return;
        _ = LoadVersionsAsync();
    }

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        if (SelectedGame is null)
        {
            Versions.Clear();
            return;
        }

        var gameId = SelectedGame.GameId;
        IReadOnlyList<CommitInfo> commits = Array.Empty<CommitInfo>();
        var ok = await RunBusyAsync(
            async () => commits = await _appService.ListVersionsAsync(gameId),
            $"正在加载「{SelectedGame.DisplayName}」的版本历史…");
        if (!ok)
            return;
        if (SelectedGame?.GameId != gameId)
            return; // 用户已切换游戏，丢弃过期结果

        Versions.Clear();
        foreach (var commit in commits.OrderByDescending(c => c.CreatedAtUtc, StringComparer.Ordinal))
            Versions.Add(new VersionRowViewModel(commit));

        if (Versions.Count == 0)
            StatusText = "该游戏暂无版本（尚未备份过）。";
    }

    [RelayCommand]
    private async Task ViewManifestAsync(VersionRowViewModel? row)
    {
        if (row is null || SelectedGame is null)
            return;
        var gameId = SelectedGame.GameId;

        Manifest? manifest = null;
        var ok = await RunBusyAsync(
            async () => manifest = await _appService.GetManifestAsync(gameId, row.Commit.ManifestHash),
            $"正在读取版本 {row.ShortId} 的文件清单…");
        if (!ok)
            return;

        if (manifest is null)
        {
            Dialog.Info($"未找到版本 {row.ShortId} 的文件清单（依赖文件可能尚未同步到本机）。");
            return;
        }

        Dialog.ShowManifest(
            $"文件清单 — 版本 {row.ShortId}（{manifest.Files.Count} 个文件）",
            manifest.Files);
    }

    [RelayCommand]
    private async Task RestoreAsync(VersionRowViewModel? row)
    {
        if (row is null || SelectedGame is null)
            return;
        var gameId = SelectedGame.GameId;

        if (!Dialog.Confirm(
                $"确定将「{SelectedGame.DisplayName}」恢复到版本 {row.ShortId} 吗？\n\n" +
                "恢复前会自动备份当前存档目录；恢复本身也是一个新版本（可再次回滚）。",
                "恢复历史版本"))
            return;

        ApplyResult? result = null;
        var ok = await RunBusyAsync(
            async () => result = await _appService.RestoreVersionAsync(gameId, row.Commit.CommitId),
            $"正在恢复到版本 {row.ShortId}…");
        if (!ok)
            return;

        if (result is { Success: true })
        {
            Dialog.Info("恢复完成。恢复前已自动备份当前存档目录。");
            await LoadVersionsAsync();
        }
        else
        {
            Dialog.Error($"恢复失败：{result?.Error ?? "未知错误"}");
        }
    }
}
