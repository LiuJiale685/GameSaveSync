// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Core.Models;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Desktop.Services;
using GameSaveSync.Sync;
using Serilog;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// 游戏页：本机游戏列表、添加游戏、从仓库绑定已有游戏（T17）、删除游戏，
/// 以及启动 / 停止游戏与运行状态显示。
/// </summary>
public sealed partial class GamesViewModel : ViewModelBase
{
    private readonly GameSaveSyncAppService _appService;
    private readonly GameProcessMonitorService _monitor;

    private IReadOnlyList<LocalGameConfig> _allGames = Array.Empty<LocalGameConfig>();

    public ObservableCollection<GameRowViewModel> Games { get; } = new();

    /// <summary>是否在列表中显示已隐藏的游戏。</summary>
    [ObservableProperty]
    private bool _showHidden;

    partial void OnShowHiddenChanged(bool value) => RebuildRows();

    /// <summary>当前选中的游戏行（右侧/下方展示其最近会话明细）。</summary>
    [ObservableProperty]
    private GameRowViewModel? _selectedGame;

    partial void OnSelectedGameChanged(GameRowViewModel? value) => _ = LoadSelectedSessionsAsync(value);

    /// <summary>选中游戏的最近会话明细。</summary>
    public ObservableCollection<PlaySessionRowViewModel> RecentSessions { get; } = new();

    [ObservableProperty]
    private string _selectedSessionsHint = "选择游戏可查看最近游戏记录。";

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
        // 轮询线程触发状态变化 → 切回 UI 线程更新行状态
        _monitor.GameStateChanged += OnGameStateChanged;
    }

    private void OnGameStateChanged(string gameId, bool isRunning)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplyState(gameId, isRunning);
        else
            dispatcher.BeginInvoke(() => ApplyState(gameId, isRunning));
    }

    private void ApplyState(string gameId, bool isRunning)
    {
        var row = Games.FirstOrDefault(r => r.GameId == gameId);
        if (row is null)
            return;

        var wasRunning = row.IsRunning;
        row.IsRunning = isRunning;

        if (!wasRunning && isRunning)
        {
            // 游戏开始运行：借这次机会提示补填缺失的存档目录
            _ = OnGameStartedAsync(row);
        }
        else if (wasRunning && !isRunning)
        {
            // 游戏退出：会话已在监控回调中收尾，刷新该游戏的时长统计
            _ = RefreshPlaytimeAsync(row);
        }
    }

    /// <summary>本次运行中用户已拒绝补填存档目录的游戏（不再重复打扰）。</summary>
    private readonly HashSet<string> _savePathPromptDeclined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 游戏开始运行：首次启动不打扰（用户可进游戏确认存档真实位置），
    /// 若此前已玩过一次且仍未设置存档目录，弹窗提示补填。
    /// </summary>
    private async Task OnGameStartedAsync(GameRowViewModel row)
    {
        if (row.HasSavePath || _savePathPromptDeclined.Contains(row.GameId))
            return;

        int finishedSessions;
        try
        {
            finishedSessions = await _appService.CountFinishedPlaySessionsAsync(row.GameId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询游戏 {GameId} 历史会话次数失败。", row.GameId);
            return;
        }

        if (finishedSessions == 0)
            return;

        await PromptMissingSavePathAsync(row);
    }

    /// <summary>
    /// 提示补填存档目录：已设置或本次运行中已拒绝过则不再打扰；
    /// 用户同意后直接打开该游戏的编辑对话框。
    /// </summary>
    private async Task PromptMissingSavePathAsync(GameRowViewModel row)
    {
        if (row.HasSavePath || _savePathPromptDeclined.Contains(row.GameId))
            return;

        await RunOnUiThreadAsync(() =>
        {
            if (!Dialog.Confirm(
                    $"游戏「{row.Name}」尚未设置存档目录，备份与同步都不会执行。\n\n现在补填存档目录吗？",
                    "需要设置存档目录"))
            {
                _savePathPromptDeclined.Add(row.GameId);
                return;
            }
            _ = EditGameAsync(row);
        });
    }

    /// <summary>在 UI 线程执行并等待完成（对话框必须在 UI 线程弹出）。</summary>
    private static async Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    /// <summary>按“是否显示已隐藏”过滤后重建列表行（保留已知运行状态）。</summary>
    private void RebuildRows()
    {
        var previousSelectionId = SelectedGame?.GameId;
        Games.Clear();
        foreach (var game in _allGames)
        {
            if (game.IsHidden && !ShowHidden)
                continue;
            Games.Add(new GameRowViewModel(game, _monitor.GetRunningState(game.GameId)));
        }
        if (previousSelectionId is not null)
            SelectedGame = Games.FirstOrDefault(r => r.GameId == previousSelectionId);

        _ = LoadAllPlaytimesAsync();
    }

    /// <summary>为全部列表行加载时长统计（不阻塞 UI）。</summary>
    private async Task LoadAllPlaytimesAsync()
    {
        var rows = Games.ToList();
        foreach (var row in rows)
        {
            await RefreshPlaytimeAsync(row);
        }
    }

    /// <summary>刷新单个游戏的时长统计。</summary>
    private async Task RefreshPlaytimeAsync(GameRowViewModel row)
    {
        try
        {
            var stats = await _appService.GetGamePlayStatsAsync(row.GameId);
            RunOnUiThread(() => row.ApplyPlayStats(stats));
            if (ReferenceEquals(SelectedGame, row))
                await LoadSelectedSessionsAsync(row);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载游戏 {GameId} 时长统计失败。", row.GameId);
        }
    }

    /// <summary>在 UI 线程执行（可观察属性 / 集合必须在 UI 线程修改）。</summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>加载选中游戏的最近会话明细。</summary>
    private async Task LoadSelectedSessionsAsync(GameRowViewModel? row)
    {
        if (row is null)
        {
            RunOnUiThread(() =>
            {
                RecentSessions.Clear();
                SelectedSessionsHint = "选择游戏可查看最近游戏记录。";
            });
            return;
        }

        IReadOnlyList<PlaySessionRecord> sessions;
        try
        {
            sessions = await _appService.GetRecentPlaySessionsAsync(row.GameId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载游戏 {GameId} 会话明细失败。", row.GameId);
            RunOnUiThread(() => SelectedSessionsHint = $"加载失败：{ex.Message}");
            return;
        }

        RunOnUiThread(() =>
        {
            RecentSessions.Clear();
            foreach (var s in sessions)
                RecentSessions.Add(new PlaySessionRowViewModel(s));
            SelectedSessionsHint = sessions.Count == 0
                ? $"「{row.Name}」暂无已记录的运行会话（少于 1 分钟不计入统计）。"
                : $"「{row.Name}」最近 {sessions.Count} 次游戏记录（少于 1 分钟的会话不计入统计）。";
        });
    }

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        IReadOnlyList<LocalGameConfig> games = Array.Empty<LocalGameConfig>();
        var ok = await RunBusyAsync(
            async () => games = await _appService.ListGamesWithSavePathAsync(),
            "正在加载游戏列表…");
        if (!ok)
            return;

        Games.Clear();
        _allGames = games;
        RebuildRows();

        // 进程监控使用最新配置（游戏退出自动备份）
        _monitor.UpdateGames(games);
        GamesChanged?.Invoke();

        // 立即刷新一次运行状态（不必等 5 秒轮询）
        try
        {
            await _monitor.RefreshStatesAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新游戏运行状态失败。");
        }
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var input = Dialog.ShowAddGameDialog(bindFromRepo: false, Array.Empty<SharedGameInfo>());
        if (input is null)
            return;

        var ok = await RunBusyAsync(
            () => _appService.CreateNewGameAsync(
                input.Name, input.SavePath, input.ProcessNames, input.ExecutablePath),
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
            () => _appService.BindExistingGameAsync(
                input.SharedGameId, input.Name, input.SavePath, input.ProcessNames, input.ExecutablePath),
            $"正在绑定游戏「{input.Name}」…");
        if (ok)
        {
            Dialog.Info($"游戏「{input.Name}」已从仓库绑定（复用共享 gameId）。");
            await LoadGamesAsync();
        }
    }

    [RelayCommand]
    private async Task EditGameAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;

        // 运行中也允许编辑：对话框已限制为“只能自动识别进程名 / 存档目录为空时补填”，
        // 这里再保守地保留其余字段的原值，避免运行期改动影响监控。
        var input = Dialog.ShowEditGameDialog(row.Config, row.IsRunning);
        if (input is null)
            return;

        // 运行中原有进程名不能被清空，否则监控会丢失该游戏的运行状态
        if (row.IsRunning && row.Config.ProcessNames.Count > 0 && input.ProcessNames.Count == 0)
        {
            Dialog.Info($"游戏「{row.Name}」正在运行，进程名不能清空。请点“自动识别”获取进程名。");
            return;
        }

        var config = new LocalGameConfig
        {
            GameId = row.GameId,
            Name = row.IsRunning ? row.Config.Name : input.Name,
            SavePath = input.SavePath,
            ProcessNames = input.ProcessNames.ToList(),
            ExecutablePath = row.IsRunning ? row.Config.ExecutablePath : input.ExecutablePath,
            AutoBackup = row.Config.AutoBackup,
            AutoRestore = row.Config.AutoRestore,
            IsHidden = row.Config.IsHidden,
        };

        var ok = await RunBusyAsync(
            () => _appService.UpdateGameAsync(config),
            $"正在保存游戏「{config.Name}」配置…");
        if (ok)
        {
            Dialog.Info($"游戏「{config.Name}」配置已更新。");
            await LoadGamesAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteGameAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsRunning)
        {
            Dialog.Info($"游戏「{row.Name}」正在运行，请先停止后再删除。");
            return;
        }
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

    /// <summary>隐藏 / 取消隐藏游戏（仅影响列表显示，不停止同步与自动备份）。</summary>
    [RelayCommand]
    private async Task ToggleHideAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;

        var hide = !row.Config.IsHidden;
        var ok = await RunBusyAsync(
            () => _appService.SetGameHiddenAsync(row.GameId, hide),
            hide ? $"正在隐藏游戏「{row.Name}」…" : $"正在取消隐藏「{row.Name}」…");
        if (!ok)
            return;

        await LoadGamesAsync();
        StatusText = hide
            ? $"已隐藏「{row.Name}」，勾选“显示已隐藏”可再次查看。"
            : $"已取消隐藏「{row.Name}」。";
    }

    /// <summary>启动 / 停止切换：未运行时启动，运行中停止。</summary>
    [RelayCommand]
    private async Task ToggleLaunchAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsRunning)
            await StopGameAsync(row);
        else
            await LaunchGameAsync(row);
    }

    /// <summary>启动游戏：校验可执行文件后由系统 Shell 启动进程。</summary>
    [RelayCommand]
    private async Task LaunchGameAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsRunning)
        {
            Dialog.Info($"游戏「{row.Name}」已在运行中。");
            return;
        }
        if (!row.CanLaunch)
        {
            Dialog.Info("该游戏尚未配置可执行文件，请先编辑游戏设置指定游戏 exe。");
            return;
        }

        var ok = await RunBusyAsync(
            () =>
            {
                _monitor.LaunchGame(row.Config.ExecutablePath);
                return Task.CompletedTask;
            },
            $"正在启动游戏「{row.Name}」…");
        if (!ok)
            return;

        StatusText = $"已启动「{row.Name}」，等待进程状态刷新…";

        // 未配置进程名时，借助运行中的进程自动识别（并同步到共享仓库）
        var detected = false;
        if (row.Config.ProcessNames.Count == 0)
        {
            try
            {
                StatusText = $"正在自动识别「{row.Name}」的进程名…";
                var names = await _monitor.DetectProcessNamesAsync(
                    row.Config.ExecutablePath, TimeSpan.FromSeconds(20));
                if (names.Count > 0)
                {
                    row.Config.ProcessNames = names.ToList();
                    await _appService.UpdateGameAsync(row.Config);
                    detected = true;
                    StatusText = $"已识别进程名：{string.Join(", ", names)}";
                }
                else
                {
                    StatusText = "未能自动识别进程名，可在“编辑”中手动填写。";
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "自动识别游戏进程名失败。");
                StatusText = "自动识别进程名失败，可在“编辑”中手动填写。";
            }
        }

        // 启动后稍等再刷新状态，让进程真正起来
        await Task.Delay(TimeSpan.FromSeconds(2));
        try
        {
            await _monitor.RefreshStatesAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新游戏运行状态失败。");
        }
        ApplyState(row.GameId, _monitor.GetRunningState(row.GameId));
        if (detected)
            await LoadGamesAsync();
    }

    /// <summary>停止游戏：按进程名优雅关闭，超时强制结束。</summary>
    [RelayCommand]
    private async Task StopGameAsync(GameRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.Config.ProcessNames.Count == 0)
        {
            Dialog.Info("该游戏未配置进程名，无法自动停止。请先编辑游戏设置填写进程名。");
            return;
        }
        if (!Dialog.Confirm($"确定停止游戏「{row.Name}」吗？\n未保存的游戏进度可能丢失。", "停止游戏"))
            return;

        var stopped = 0;
        var ok = await RunBusyAsync(
            () =>
            {
                stopped = _monitor.StopGame(row.Config.ProcessNames);
                return Task.CompletedTask;
            },
            $"正在停止游戏「{row.Name}」…");
        if (!ok)
            return;

        await Task.Delay(TimeSpan.FromSeconds(1));
        try
        {
            await _monitor.RefreshStatesAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新游戏运行状态失败。");
        }
        ApplyState(row.GameId, _monitor.GetRunningState(row.GameId));

        if (stopped == 0)
            Dialog.Info("未找到正在运行的游戏进程（可能已退出）。");
        else
        {
            StatusText = $"已停止「{row.Name}」（{stopped} 个进程）。";
            // 游戏刚退出、存档刚落盘，是补填存档目录最好的时机
            await PromptMissingSavePathAsync(row);
        }
    }

    /// <summary>导出全部游戏的时长与会话明细为 CSV。</summary>
    [RelayCommand]
    private async Task ExportPlaytimeAsync()
    {
        var path = Dialog.PickSaveFile("导出游戏时长数据", "CSV 文件 (*.csv)|*.csv",
            $"游戏时长_{DateTime.Now:yyyyMMdd}.csv");
        if (string.IsNullOrWhiteSpace(path))
            return;

        var ok = await RunBusyAsync(
            () => _appService.ExportPlaySessionsCsvAsync(path),
            "正在导出游戏时长数据…");
        if (ok)
            Dialog.Info($"游戏时长数据已导出：\n{path}");
    }

    /// <summary>刷新运行状态（手动触发一次轮询检查）。</summary>    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        try
        {
            await _monitor.RefreshStatesAsync();
            StatusText = "运行状态已刷新。";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新游戏运行状态失败。");
            StatusText = $"刷新失败：{ex.Message}";
        }
    }
}
