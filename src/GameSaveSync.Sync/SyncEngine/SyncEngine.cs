// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Interfaces;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Data;
using GameSaveSync.Data.Repositories;
using GameSaveSync.FileSystem;
using GameSaveSync.Snapshot;
using GameSaveSync.Storage;
using Serilog;

namespace GameSaveSync.Sync.SyncEngine;

/// <summary>
/// 同步引擎（方案 5.3 / 7.3）：扫描仓库、校验远端版本依赖、
/// 结合本地状态给出同步决策。绝不静默覆盖用户当前存档。
/// </summary>
public sealed class SyncEngine
{
    private readonly RepoFileService _repoFiles;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly RestoreManager.RestoreManager _restoreManager;
    private readonly FileScanner _scanner = new();
    private readonly IGameProcessChecker _processChecker;

    private readonly DeviceInfoRecord _device;
    private readonly GameRepository _games;
    private readonly AppliedVersionsRepository _applied;
    private readonly CommitCacheRepository _commitCache;
    private readonly ConflictRepository _conflicts;

    public SyncEngine(
        RepoFileService repoFiles,
        SnapshotBuilder snapshotBuilder,
        RestoreManager.RestoreManager restoreManager,
        IGameProcessChecker processChecker,
        DeviceInfoRecord device,
        GameRepository games,
        AppliedVersionsRepository applied,
        CommitCacheRepository commitCache,
        ConflictRepository conflicts)
    {
        _repoFiles = repoFiles;
        _snapshotBuilder = snapshotBuilder;
        _restoreManager = restoreManager;
        _processChecker = processChecker;
        _device = device;
        _games = games;
        _applied = applied;
        _commitCache = commitCache;
        _conflicts = conflicts;
    }

    /// <summary>单游戏同步报告/决策（方案 7.3 条件表）。</summary>
    public sealed record GameSyncReport
    {
        public string GameId { get; init; } = string.Empty;

        public SyncStatus Status { get; init; }

        /// <summary>当前全部有效 Head。</summary>
        public IReadOnlyList<CommitInfo> Heads { get; init; } = Array.Empty<CommitInfo>();

        /// <summary>依赖未到齐、等待同步的 Commit。</summary>
        public IReadOnlyList<CommitInfo> WaitingCommits { get; init; } = Array.Empty<CommitInfo>();

        public LocalDirectoryState LocalState { get; init; }

        public string? LastAppliedCommitId { get; init; }

        /// <summary>建议自动应用的远端 Commit（单一 Head 且本地干净等条件满足）。</summary>
        public CommitInfo? AutoApplyCandidate { get; init; }

        /// <summary>触发冲突的 Head 集合。</summary>
        public IReadOnlyList<CommitInfo> ConflictHeads { get; init; } = Array.Empty<CommitInfo>();

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 扫描指定游戏的仓库状态并给出同步决策。
    /// </summary>
    /// <param name="gameId">共享 gameId。</param>
    /// <param name="savePath">本机存档目录。</param>
    /// <param name="processNames">游戏进程名（可选）。</param>
    /// <param name="executablePath">游戏程序路径（可选，用于区分同名进程）。</param>
    public async Task<GameSyncReport> ScanGameAsync(
        string gameId,
        string savePath,
        IReadOnlyList<string>? processNames = null,
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        // 1. 扫描仓库全部 Commit（跨设备）并缓存
        var commits = await _repoFiles.ListCommitsAsync(gameId, cancellationToken).ConfigureAwait(false);
        foreach (var c in commits)
            await _commitCache.UpsertAsync(c, localOrigin: false, cancellationToken).ConfigureAwait(false);

        var graph = CommitGraph.Analyze(commits);
        var applied = await _applied.GetAsync(gameId, cancellationToken).ConfigureAwait(false);

        var waiting = new List<CommitInfo>();
        var verifiedHeads = new List<CommitInfo>();

        // 2. 校验每个 Head 的依赖完整性（方案 5.2）：Manifest 存在 + Object 齐（大小校验）
        foreach (var head in graph.Heads)
        {
            var manifest = await _repoFiles.TryReadManifestAsync(gameId, head.ManifestHash, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                waiting.Add(head);
                continue;
            }
            var broken = await _snapshotBuilder
                .FindBrokenObjectsAsync(manifest, verifyHash: false, cancellationToken)
                .ConfigureAwait(false);
            if (broken.Count > 0)
                waiting.Add(head);
            else
                verifiedHeads.Add(head);
        }

        // 父版本未到齐的 Commit 也计入等待
        waiting.AddRange(graph.PendingNodes.Select(n => n.Commit));

        // 3. 本地状态判断
        var localState = await EvaluateLocalStateAsync(gameId, applied, savePath, cancellationToken)
            .ConfigureAwait(false);

        // 4. 游戏运行检查（T08：运行期间禁止自动覆盖）
        var gameRunning = processNames is { Count: > 0 }
            && await _processChecker
                .IsAnyRunningAsync(processNames, executablePath, cancellationToken).ConfigureAwait(false);

        var report = new GameSyncReport
        {
            GameId = gameId,
            Heads = graph.Heads,
            WaitingCommits = waiting,
            LocalState = localState,
            LastAppliedCommitId = applied?.CommitId,
        };

        if (gameRunning)
        {
            return report with
            {
                Status = verifiedHeads.Count > 0 && !AreHeadsEqualToApplied(verifiedHeads, applied)
                    ? SyncStatus.ConflictDetected : SyncStatus.LocalSnapshotReady,
                ConflictHeads = verifiedHeads,
                Message = "游戏正在运行，延迟应用，禁止覆盖存档。",
            };
        }

        if (waiting.Count > 0)
        {
            return report with
            {
                Status = SyncStatus.WaitingForFiles,
                Message = $"{waiting.Count} 个远端版本依赖文件尚未同步到本机，等待中。",
            };
        }

        if (verifiedHeads.Count == 0)
        {
            return report with
            {
                Status = SyncStatus.LocalSnapshotReady,
                Message = "仓库中没有可用的远端版本。",
            };
        }

        // 5. 多 Head → 冲突（方案 7.2）
        if (verifiedHeads.Count > 1)
        {
            await RecordConflictAsync(gameId, applied?.CommitId, verifiedHeads, cancellationToken)
                .ConfigureAwait(false);
            return report with
            {
                Status = SyncStatus.ConflictDetected,
                ConflictHeads = verifiedHeads,
                Message = $"检测到 {verifiedHeads.Count} 个并行版本（多 Head），需要用户处理冲突。",
            };
        }

        // 6. 单 Head
        var head0 = verifiedHeads[0];
        var headIsApplied = applied is not null
            && string.Equals(head0.CommitId, applied.CommitId, StringComparison.OrdinalIgnoreCase);

        if (headIsApplied)
        {
            return report with
            {
                Status = SyncStatus.RemoteVersionVerified,
                Message = "本地已处于最新版本。",
            };
        }

        if (applied is null)
        {
            // 本地基线不明确：要求用户确认，不自动覆盖（方案 7.3）
            return report with
            {
                Status = SyncStatus.ConflictDetected,
                ConflictHeads = new[] { head0 },
                Message = "本地没有已应用的版本基线，需用户确认后才能应用远端版本。",
            };
        }

        // 远端是否为本地已应用版本的后代（顺序同步，方案 7.1）
        var isDescendant = CommitGraph.IsDescendantOf(graph.Nodes, head0.CommitId, applied.CommitId);
        if (!isDescendant)
        {
            await RecordConflictAsync(gameId, applied.CommitId, new[] { head0 }, cancellationToken)
                .ConfigureAwait(false);
            return report with
            {
                Status = SyncStatus.ConflictDetected,
                ConflictHeads = new[] { head0 },
                Message = "远端版本与本地版本是并行提交，需要冲突处理。",
            };
        }

        if (localState == LocalDirectoryState.Dirty)
        {
            return report with
            {
                Status = SyncStatus.ConflictDetected,
                ConflictHeads = new[] { head0 },
                Message = "本地存在未提交修改，须先保存为快照再处理。",
            };
        }

        // 单一远端 Head、本地无变化、游戏未运行 → 可自动应用（方案 7.3 条件表第一行）
        if (localState is LocalDirectoryState.Clean or LocalDirectoryState.EmptyOrMissing)
        {
            return report with
            {
                Status = SyncStatus.RemoteVersionVerified,
                AutoApplyCandidate = head0,
                Message = "远端新版本依赖完整且本地无修改，可自动应用。",
            };
        }

        return report with
        {
            Status = SyncStatus.ConflictDetected,
            ConflictHeads = new[] { head0 },
            Message = "本地基线不明确，需要用户确认。",
        };
    }

    /// <summary>
    /// 应用远端版本到本地（快进，方案 7.1）。
    /// 调用方须确认 ScanGameAsync 给出 AutoApplyCandidate 或用户已明确选择。
    /// </summary>
    public async Task ApplyRemoteVersionAsync(
        string gameId,
        string commitId,
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var result = await _restoreManager
            .ApplyCommitAsync(gameId, commitId, savePath, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new RepoIntegrityException($"应用远端版本失败: {result.Error}");

        var commit = await FindCommitAsync(gameId, commitId, cancellationToken).ConfigureAwait(false);
        await _applied.SetAsync(gameId, commit.CommitId, commit.ManifestHash, cancellationToken)
            .ConfigureAwait(false);
        Log.Information("远端版本 {CommitId} 已应用到本地（game {GameId}）。", commitId, gameId);
    }

    /// <summary>重新扫描本地目录判断是否与上次应用的 Manifest 一致（方案 7.3 第 2 步）。</summary>
    public async Task<LocalDirectoryState> EvaluateLocalStateAsync(
        string gameId,
        AppliedVersionRecord? applied,
        string savePath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(savePath))
            return applied is null ? LocalDirectoryState.UnknownBaseline : LocalDirectoryState.EmptyOrMissing;

        var manifest = applied is null
            ? null
            : await _repoFiles.TryReadManifestAsync(gameId, applied.ManifestHash, cancellationToken)
                .ConfigureAwait(false);

        if (manifest is null)
            return LocalDirectoryState.UnknownBaseline;

        var scanned = _scanner.Scan(savePath, cancellationToken);
        if (scanned.Count == 0 && manifest.Files.Count == 0)
            return LocalDirectoryState.Clean;
        if (scanned.Count != manifest.Files.Count)
            return LocalDirectoryState.Dirty;

        var expected = manifest.Files.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in scanned)
        {
            if (!expected.TryGetValue(file.RelativePath, out var entry))
                return LocalDirectoryState.Dirty;
            if (file.Size != entry.Size)
                return LocalDirectoryState.Dirty;
            // 大小一致但不确定内容时用哈希复核（避免 mtime 误差误报）
            if (entry.LastWriteUtc is not null
                && UtcClock.TryParse(entry.LastWriteUtc, out var mtime)
                && Math.Abs((file.LastWriteUtc - mtime).TotalSeconds) < 2)
            {
                continue;
            }
            var sha = await FileHasher.ComputeSha256Async(file.FullPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(sha, entry.Sha256, StringComparison.Ordinal))
                return LocalDirectoryState.Dirty;
        }
        return LocalDirectoryState.Clean;
    }

    private static bool AreHeadsEqualToApplied(
        IReadOnlyList<CommitInfo> heads, AppliedVersionRecord? applied)
        => applied is not null
           && heads.Count == 1
           && string.Equals(heads[0].CommitId, applied.CommitId, StringComparison.OrdinalIgnoreCase);

    private async Task RecordConflictAsync(
        string gameId,
        string? localCommitId,
        IReadOnlyList<CommitInfo> remoteHeads,
        CancellationToken cancellationToken)
    {
        var openConflicts = await _conflicts.ListOpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = openConflicts.FirstOrDefault(c => c.GameId == gameId);
        if (existing is not null)
            return; // 已有未处理冲突记录
        await _conflicts.AddAsync(new ConflictRecord(
            Guid.NewGuid().ToString("D"),
            gameId,
            localCommitId,
            remoteHeads.Select(h => h.CommitId).ToList(),
            ConflictStatusValue.Open,
            UtcClock.Now(),
            null), cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommitInfo> FindCommitAsync(
        string gameId, string commitId, CancellationToken cancellationToken)
    {
        var commits = await _repoFiles.ListCommitsAsync(gameId, cancellationToken).ConfigureAwait(false);
        return commits.FirstOrDefault(c => string.Equals(c.CommitId, commitId, StringComparison.OrdinalIgnoreCase))
            ?? throw new RepoIntegrityException($"Commit {commitId} 不存在（game {gameId}）。");
    }
}
