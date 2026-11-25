// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Core.Models;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Desktop.Services;
using GameSaveSync.Sync;
using GameSyncReport = GameSaveSync.Sync.SyncEngine.SyncEngine.GameSyncReport;
using ResolutionResult = GameSaveSync.Sync.ConflictResolver.ConflictResolver.ResolutionResult;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// 同步状态页：每游戏扫描同步决策（状态/Message/Heads/等待数）、
/// 自动应用远端版本（AutoApplyCandidate）、冲突列表三选一处理（方案 7.4）。
/// </summary>
public sealed partial class SyncStatusViewModel : ViewModelBase
{
    private readonly GameSaveSyncAppService _appService;

    public ObservableCollection<GameOptionViewModel> Games { get; } = new();

    [ObservableProperty]
    private GameOptionViewModel? _selectedGame;

    // ---- 同步报告展示 ----
    [ObservableProperty] private string _statusDisplay = "—";
    [ObservableProperty] private SyncStatus? _reportStatus;
    [ObservableProperty] private string _reportMessage = string.Empty;
    [ObservableProperty] private string _headsDisplay = "—";
    [ObservableProperty] private string _waitingDisplay = "—";
    [ObservableProperty] private string _localStateDisplay = "—";
    [ObservableProperty] private string _lastAppliedDisplay = "—";
    [ObservableProperty] private bool _hasCandidate;
    [ObservableProperty] private string _candidateDisplay = "无";

    private CommitInfo? _candidateCommit;

    // ---- 冲突列表 ----
    public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = new();

    [ObservableProperty]
    private string _conflictSummary = "当前没有未处理的冲突。";

    /// <summary>是否存在未处理冲突（驱动空状态切换）。</summary>
    [ObservableProperty]
    private bool _hasConflicts;

    public SyncStatusViewModel(GameSaveSyncAppService appService, IDialogService dialog)
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

        SelectedGame = Games.FirstOrDefault(g => g.GameId == previousId) ?? Games.FirstOrDefault();
        ResetReport();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedGame is null)
        {
            Dialog.Info("请先选择要扫描的游戏。");
            return;
        }
        var gameId = SelectedGame.GameId;

        GameSyncReport? report = null;
        var ok = await RunBusyAsync(
            async () => report = await _appService.ScanGameAsync(gameId),
            $"正在扫描「{SelectedGame.DisplayName}」的同步状态…");
        if (!ok)
            return;

        ApplyReport(report);
    }

    [RelayCommand]
    private async Task ApplyRemoteAsync()
    {
        var candidate = _candidateCommit;
        if (candidate is null || SelectedGame is null)
        {
            Dialog.Info("当前没有建议自动应用的远端版本，请先扫描。");
            return;
        }
        var gameId = SelectedGame.GameId;

        if (!Dialog.Confirm(
                $"确定将远端版本 {FormatUtil.ShortId(candidate.CommitId)} 应用到「{SelectedGame.DisplayName}」的存档目录吗？\n\n" +
                "本地目录干净、远端为唯一有效 Head 时才可应用；应用会替换本地存档内容。",
                "应用远端版本"))
            return;

        var ok = await RunBusyAsync(
            () => _appService.ApplyRemoteVersionAsync(gameId, candidate.CommitId),
            $"正在应用远端版本 {FormatUtil.ShortId(candidate.CommitId)}…");
        if (!ok)
            return;

        Dialog.Info("远端版本已应用到本地存档目录。");
        await ScanAsync();
        await RefreshConflictsAsync();
    }

    [RelayCommand]
    private async Task RefreshConflictsAsync()
    {
        IReadOnlyList<ConflictRecord> conflicts = Array.Empty<ConflictRecord>();
        var ok = await RunBusyAsync(
            async () => conflicts = await _appService.ListConflictsAsync(),
            "正在加载冲突列表…");
        if (!ok)
            return;

        Conflicts.Clear();
        foreach (var conflict in conflicts)
            Conflicts.Add(new ConflictRowViewModel(conflict));
        HasConflicts = conflicts.Count > 0;
        ConflictSummary = conflicts.Count == 0
            ? "当前没有未处理的冲突。"
            : $"{conflicts.Count} 个未处理冲突";
    }

    // ---------- 冲突三选一（方案 7.4） ----------

    [RelayCommand]
    private async Task KeepLocalAsync(ConflictRowViewModel? row)
        => await ResolveAsync(row, ConflictResolution.KeepLocal, null, "保留本机版本");

    [RelayCommand]
    private async Task KeepBothAsync(ConflictRowViewModel? row)
        => await ResolveAsync(row, ConflictResolution.KeepBothToTemp, null, "分别保留到临时目录");

    [RelayCommand]
    private async Task UseRemoteAsync(ConflictRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.Record.RemoteCommitIds.Count == 0)
        {
            Dialog.Error("该冲突没有可选的远端版本。");
            return;
        }

        // 用户从冲突的 RemoteCommitIds 中选择一个（展示前 8 位，映射回完整 id）
        var displayList = row.Record.RemoteCommitIds
            .Select(id => FormatUtil.ShortId(id))
            .ToList();
        var picked = Dialog.PickItem("请选择要使用的云端版本（CommitId 前 8 位）：", displayList);
        if (picked is null)
            return;
        var remoteCommitId = row.Record.RemoteCommitIds
            .FirstOrDefault(id => FormatUtil.ShortId(id) == picked);
        if (remoteCommitId is null)
        {
            Dialog.Error("无法解析所选的云端版本，请重试。");
            return;
        }

        await ResolveAsync(row, ConflictResolution.UseRemote, remoteCommitId, $"使用云端版本 {picked}");
    }

    private async Task ResolveAsync(
        ConflictRowViewModel? row, ConflictResolution resolution, string? remoteCommitId, string actionText)
    {
        if (row is null)
            return;
        if (!Dialog.Confirm(
                $"确定对冲突 {FormatUtil.ShortId(row.Record.ConflictId)}（游戏 {row.GameDisplay}）执行「{actionText}」吗？",
                "处理冲突"))
            return;

        ResolutionResult? result = null;
        var ok = await RunBusyAsync(
            async () => result = await _appService.ResolveConflictAsync(
                row.Record.ConflictId, resolution, remoteCommitId),
            $"正在处理冲突（{actionText}）…");
        if (!ok)
            return;

        ShowResolutionResult(result);
        await RefreshConflictsAsync();
    }

    private void ShowResolutionResult(ResolutionResult? result)
    {
        if (result is null)
            return;
        if (!result.Success)
        {
            Dialog.Error($"冲突处理失败：{result.Message ?? "未知原因"}");
            return;
        }

        var lines = new List<string> { result.Message ?? "冲突处理完成。" };
        if (result.NewCommit is not null)
            lines.Add($"生成本地新版本：{FormatUtil.ShortId(result.NewCommit.CommitId)}");
        if (result.TempExportPaths is { Count: > 0 })
        {
            lines.Add("已将两个版本分别导出到临时目录：");
            lines.AddRange(result.TempExportPaths.Select(p => "  " + p));
        }
        Dialog.Info(string.Join(Environment.NewLine, lines));
    }

    // ---------- 报告展示 ----------

    private void ResetReport()
    {
        StatusDisplay = "—";
        ReportStatus = null;
        ReportMessage = string.Empty;
        HeadsDisplay = "—";
        WaitingDisplay = "—";
        LocalStateDisplay = "—";
        LastAppliedDisplay = "—";
        _candidateCommit = null;
        HasCandidate = false;
        CandidateDisplay = "无";
    }

    private void ApplyReport(GameSyncReport? report)
    {
        if (report is null)
        {
            ResetReport();
            return;
        }
        StatusDisplay = Describe(report.Status);
        ReportStatus = report.Status;
        ReportMessage = report.Message;
        HeadsDisplay = report.Heads.Count == 0
            ? "无"
            : string.Join("、", report.Heads.Select(h => FormatUtil.ShortId(h.CommitId)));
        WaitingDisplay = report.WaitingCommits.Count == 0
            ? "无"
            : string.Join("、", report.WaitingCommits.Select(h => FormatUtil.ShortId(h.CommitId)));
        LocalStateDisplay = Describe(report.LocalState);
        LastAppliedDisplay = FormatUtil.ShortId(report.LastAppliedCommitId);

        _candidateCommit = report.AutoApplyCandidate;
        HasCandidate = report.AutoApplyCandidate is not null;
        CandidateDisplay = HasCandidate
            ? $"{FormatUtil.ShortId(_candidateCommit!.CommitId)} — {_candidateCommit.Message ?? "（无说明）"}"
            : "无";
    }

    private static string Describe(SyncStatus status) => status switch
    {
        SyncStatus.LocalSnapshotReady => "本地快照已生成",
        SyncStatus.WaitingForCloudSync => "已写入同步目录，等待云端上传",
        SyncStatus.RemoteVersionDiscovered => "发现远端新版本",
        SyncStatus.RemoteVersionVerified => "远端版本已校验完整",
        SyncStatus.ConflictDetected => "检测到冲突",
        SyncStatus.WaitingForFiles => "依赖文件尚未同步到本机",
        SyncStatus.RestorePending => "等待安全恢复",
        SyncStatus.Failed => "操作失败",
        _ => status.ToString(),
    };

    private static string Describe(LocalDirectoryState state) => state switch
    {
        LocalDirectoryState.Clean => "与基线一致（干净）",
        LocalDirectoryState.Dirty => "有未提交的本地修改",
        LocalDirectoryState.UnknownBaseline => "尚无基线（从未应用版本）",
        LocalDirectoryState.EmptyOrMissing => "目录不存在或为空",
        _ => state.ToString(),
    };
}
