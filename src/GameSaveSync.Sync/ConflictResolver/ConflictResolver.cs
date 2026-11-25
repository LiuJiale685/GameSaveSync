// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Data;
using GameSaveSync.Snapshot;
using GameSaveSync.Storage;
using GameSaveSync.Sync.CommitManager;
using GameSaveSync.Sync.RestoreManager;
using Serilog;

namespace GameSaveSync.Sync.ConflictResolver;

/// <summary>
/// 冲突处理（方案 7.4 / 7.5）：三个 MVP 选项，处理过程绝不静默覆盖任一侧数据。
/// </summary>
public sealed class ConflictResolver
{
    private readonly CommitManager.CommitManager _commits;
    private readonly RestoreManager.RestoreManager _restore;
    private readonly RepoFileService _repoFiles;
    private readonly ObjectStore _objectStore;
    private readonly ConflictRepository _conflicts;
    private readonly AppliedVersionsRepository _applied;

    public ConflictResolver(
        CommitManager.CommitManager commits,
        RestoreManager.RestoreManager restore,
        RepoFileService repoFiles,
        ObjectStore objectStore,
        ConflictRepository conflicts,
        AppliedVersionsRepository applied)
    {
        _commits = commits;
        _restore = restore;
        _repoFiles = repoFiles;
        _objectStore = objectStore;
        _conflicts = conflicts;
        _applied = applied;
    }

    /// <summary>冲突处理结果。</summary>
    public sealed record ResolutionResult
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
        public CommitInfo? NewCommit { get; init; }
        public IReadOnlyList<string>? TempExportPaths { get; init; }
    }

    /// <summary>
    /// 处理冲突（方案 7.4）。
    /// </summary>
    /// <param name="selectedRemoteCommitId">UseRemote 时用户选择的远端版本。</param>
    public async Task<ResolutionResult> ResolveAsync(
        string conflictId,
        ConflictResolution resolution,
        string? selectedRemoteCommitId,
        string gameId,
        string deviceId,
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var conflicts = await _conflicts.ListOpenAsync(cancellationToken).ConfigureAwait(false);
        var conflict = conflicts.FirstOrDefault(c => c.ConflictId == conflictId)
            ?? throw new InvalidOperationException($"冲突记录 {conflictId} 不存在或已处理。");

        try
        {
            ResolutionResult result = resolution switch
            {
                ConflictResolution.KeepLocal => await KeepLocalAsync(
                    gameId, deviceId, savePath, conflict, cancellationToken).ConfigureAwait(false),
                ConflictResolution.UseRemote => await UseRemoteAsync(
                    gameId, deviceId, savePath, conflict, selectedRemoteCommitId, cancellationToken)
                    .ConfigureAwait(false),
                ConflictResolution.KeepBothToTemp => await KeepBothToTempAsync(
                    gameId, savePath, conflict, cancellationToken).ConfigureAwait(false),
                _ => new ResolutionResult { Success = false, Message = "未知的冲突处理方式。" },
            };

            if (result.Success)
            {
                await _conflicts.UpdateStatusAsync(conflictId, ConflictStatusValue.Resolved, cancellationToken)
                    .ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "冲突处理失败（conflict {ConflictId}）。", conflictId);
            return new ResolutionResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// 选项 1：保留本机版本 — 本机当前存档创建为新 Commit，parentIds 包含全部并行 Head
    /// （方案 7.5：V3 的 parentIds 同时包含 V2-A 和 V2-B，收敛版本关系）。
    /// </summary>
    private async Task<ResolutionResult> KeepLocalAsync(
        string gameId, string deviceId, string savePath,
        ConflictRecord conflict, CancellationToken cancellationToken)
    {
        var parentIds = new List<string>(conflict.RemoteCommitIds);
        if (conflict.LocalCommitId is not null)
            parentIds.Add(conflict.LocalCommitId);

        var commit = await _commits.CreateCommitAsync(
            gameId, deviceId, savePath, parentIds.Distinct().ToList(),
            "冲突处理：保留本机版本（合并父版本）", cancellationToken).ConfigureAwait(false);

        await _applied.SetAsync(gameId, commit.CommitId, commit.ManifestHash, cancellationToken)
            .ConfigureAwait(false);

        return new ResolutionResult
        {
            Success = true,
            NewCommit = commit,
            Message = $"本机版本已保存为新 Commit {commit.CommitId[..8]}…，与远端并行版本收敛。",
        };
    }

    /// <summary>
    /// 选项 2：使用云端版本 — 先快照备份本机当前存档（进入版本历史），再应用选定的远端版本。
    /// </summary>
    private async Task<ResolutionResult> UseRemoteAsync(
        string gameId, string deviceId, string savePath,
        ConflictRecord conflict, string? selectedRemoteCommitId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(selectedRemoteCommitId))
            return new ResolutionResult { Success = false, Message = "未指定要使用的远端版本。" };

        // 先快照备份本机当前存档（不覆盖、不丢失）
        var backupCommit = await _commits.CreateCommitAsync(
            gameId, deviceId, savePath,
            conflict.LocalCommitId is null
                ? Array.Empty<string>()
                : new[] { conflict.LocalCommitId },
            "冲突处理：应用远端版本前的本机备份", cancellationToken).ConfigureAwait(false);

        await _restore.ApplyCommitAsync(gameId, selectedRemoteCommitId, savePath, cancellationToken)
            .ConfigureAwait(false);

        var remote = await _applied.GetAsync(gameId, cancellationToken).ConfigureAwait(false);
        var manifestHash = remote?.ManifestHash
            ?? (await _repoFiles.ListCommitsAsync(gameId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.CommitId, selectedRemoteCommitId, StringComparison.OrdinalIgnoreCase))
            ?.ManifestHash
            ?? throw new RepoIntegrityException("应用远端版本后未能确定 Manifest。");

        var commit = await _commits.CreateCommitReferencingExistingManifestAsync(
            gameId, deviceId, manifestHash,
            new[] { backupCommit.CommitId, selectedRemoteCommitId }.Distinct().ToList(),
            "冲突处理：采用云端版本（收敛父版本）", cancellationToken).ConfigureAwait(false);

        await _applied.SetAsync(gameId, commit.CommitId, commit.ManifestHash, cancellationToken)
            .ConfigureAwait(false);

        return new ResolutionResult
        {
            Success = true,
            NewCommit = commit,
            Message = $"已应用云端版本，本机原状态备份为 Commit {backupCommit.CommitId[..8]}…。",
        };
    }

    /// <summary>
    /// 选项 3：分别保留两个版本 — 将本机当前存档与各远端版本分别构建到临时目录供用户检查。
    /// </summary>
    private async Task<ResolutionResult> KeepBothToTempAsync(
        string gameId, string savePath, ConflictRecord conflict, CancellationToken cancellationToken)
    {
        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameSaveSync", "conflict-export", conflict.ConflictId);
        Directory.CreateDirectory(exportRoot);
        var exported = new List<string>();

        // 本机当前状态
        var localDir = Path.Combine(exportRoot, "local-current");
        CopyDirectory(savePath, localDir);
        exported.Add(localDir);

        foreach (var commitId in conflict.RemoteCommitIds)
        {
            var manifest = await _repoFiles
                .TryReadManifestByCommitAsync(gameId, commitId, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
                continue;
            var targetDir = Path.Combine(exportRoot, $"remote-{commitId[..8]}");
            await ExportManifestAsync(manifest, targetDir, cancellationToken).ConfigureAwait(false);
            exported.Add(targetDir);
        }

        return new ResolutionResult
        {
            Success = true,
            TempExportPaths = exported,
            Message = $"各版本已导出到 {exportRoot}，检查后请选择“保留本机”或“使用云端”完成收敛。",
        };
    }

    private async Task ExportManifestAsync(
        Manifest manifest, string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var entry in manifest.Files)
        {
            var target = Path.Combine(targetDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = await _objectStore.OpenReadAsync(entry.Sha256, ct).ConfigureAwait(false);
            await using var output = File.Create(target);
            await source.CopyToAsync(output, ct).ConfigureAwait(false);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(
            source, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
