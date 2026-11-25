// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Text.Json;
using System.Text.Json.Serialization;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Data.Repositories;
using GameSaveSync.FileSystem;
using GameSaveSync.Snapshot;
using GameSaveSync.Storage;
using Serilog;

namespace GameSaveSync.Sync.RestoreManager;

/// <summary>
/// 恢复/应用版本管理器（方案 8.1 / 8.2 / T14）：
/// staging 构建 → 恢复前备份 → 按 Journal 执行文件级替换 → 完成后校验。
/// 崩溃后根据 Journal 回滚（保守策略：未完成的恢复一律回滚，不“继续”）。
/// </summary>
public sealed class RestoreManager
{
    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly RepoFileService _repoFiles;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly ObjectStore _objectStore;
    private readonly RestoreJournalRepository _journals;

    public RestoreManager(
        RepoFileService repoFiles,
        SnapshotBuilder snapshotBuilder,
        ObjectStore objectStore,
        RestoreJournalRepository journals)
    {
        _repoFiles = repoFiles;
        _snapshotBuilder = snapshotBuilder;
        _objectStore = objectStore;
        _journals = journals;
    }

    public string TempRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameSaveSync", "temp");

    /// <summary>Journal 单步记录。</summary>
    public sealed class JournalEntry
    {
        [JsonPropertyOrder(0)]
        public string Op { get; set; } = string.Empty; // backup | write | delete | mtime

        [JsonPropertyOrder(1)]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyOrder(2)]
        public string? Backup { get; set; }

        [JsonPropertyOrder(3)]
        public string? LastWriteUtc { get; set; }
    }

    /// <summary>应用结果。</summary>
    public sealed class ApplyResult
    {
        public bool Success { get; init; }
        public string RestoreId { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    /// <summary>
    /// 将目标版本（Commit → Manifest）安全应用到游戏存档目录。
    /// 前置条件：调用方已确认依赖 Object 完整（本方法再次做完整哈希校验）。
    /// </summary>
    public async Task<ApplyResult> ApplyCommitAsync(
        string gameId,
        string commitId,
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var commit = await GetCommitAsync(gameId, commitId, cancellationToken).ConfigureAwait(false);
        var manifest = await _repoFiles
            .TryReadManifestAsync(gameId, commit.ManifestHash, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RepoIntegrityException($"Manifest {commit.ManifestHash} 不存在。");

        return await ApplyManifestAsync(gameId, commitId, manifest, savePath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ApplyResult> ApplyManifestAsync(
        string gameId,
        string targetCommitId,
        Manifest manifest,
        string savePath,
        CancellationToken cancellationToken = default)
    {
        // 完整性校验：逐个 Object 完整 SHA256（方案 5.2 / T05）。
        var broken = await _snapshotBuilder
            .FindBrokenObjectsAsync(manifest, verifyHash: true, cancellationToken)
            .ConfigureAwait(false);
        if (broken.Count > 0)
            throw new RepoIntegrityException(
                $"目标版本依赖的 {broken.Count} 个 Object 缺失或校验失败，禁止恢复。");

        var restoreId = Guid.NewGuid().ToString("D");
        var stagingDir = Path.Combine(TempRoot, $"staging-{restoreId}");
        var backupDir = Path.Combine(TempRoot, $"backup-{restoreId}");
        var journalsDir = RestoreJournalRepository.JournalsDirectory();
        var journalPath = Path.Combine(journalsDir, $"{restoreId}.json");

        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(savePath);

        await _journals.AddAsync(new RestoreJournalRecord(
            restoreId, gameId, targetCommitId, RestoreJournalStatus.InProgress,
            journalPath, UtcClock.Now(), UtcClock.Now()), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // 1. 构建 staging（独立目录，不触碰游戏目录）
            await BuildStagingAsync(manifest, stagingDir, cancellationToken).ConfigureAwait(false);

            // 2. 备份现有目录内容（逐文件记录 Journal）
            var journal = new List<JournalEntry>();
            await BackupCurrentDirectoryAsync(savePath, backupDir, journal, journalPath, cancellationToken)
                .ConfigureAwait(false);

            // 3. 逐文件替换 + 删除多余文件，每步记录 Journal
            await ReplaceFilesAsync(manifest, stagingDir, savePath, backupDir, journal, journalPath, cancellationToken)
                .ConfigureAwait(false);

            // 4. 完成后重新扫描并校验与 Manifest 一致（方案 8.1 步骤 8）
            await VerifyDirectoryAsync(manifest, savePath, cancellationToken).ConfigureAwait(false);

            await _journals.UpdateStatusAsync(restoreId, RestoreJournalStatus.Completed, cancellationToken)
                .ConfigureAwait(false);
            CleanupDir(stagingDir);
            CleanupDir(backupDir);
            TryDeleteFile(journalPath);

            return new ApplyResult { Success = true, RestoreId = restoreId };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "恢复 {RestoreId} 失败，尝试回滚。", restoreId);
            string? rollbackError = null;
            try
            {
                await RollbackAsync(savePath, backupDir, cancellationToken).ConfigureAwait(false);
                await _journals.UpdateStatusAsync(restoreId, RestoreJournalStatus.RolledBack, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception rbEx)
            {
                rollbackError = rbEx.Message;
                Log.Error(rbEx, "恢复 {RestoreId} 回滚失败，备份目录保留在 {BackupDir}", restoreId, backupDir);
            }

            CleanupDir(stagingDir);
            return new ApplyResult
            {
                Success = false,
                RestoreId = restoreId,
                Error = $"{ex.Message}" + (rollbackError is null ? "" : $"（回滚失败：{rollbackError}，备份保留于 {backupDir}）"),
            };
        }
    }

    /// <summary>
    /// 启动时处理未完成的恢复（T14 / 方案 8.2）：根据备份目录回滚到恢复前状态。
    /// 存档路径由调用方（App 层）根据本地配置解析后传入。
    /// </summary>
    public async Task RollbackPendingRestoreAsync(
        RestoreJournalRecord record, string savePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var backupDir = Path.Combine(TempRoot, $"backup-{record.RestoreId}");
            await RollbackAsync(savePath, backupDir, cancellationToken).ConfigureAwait(false);
            await _journals.UpdateStatusAsync(record.RestoreId, RestoreJournalStatus.RolledBack, cancellationToken)
                .ConfigureAwait(false);
            TryDeleteFile(record.JournalPath);
            Log.Information("未完成恢复 {RestoreId} 已回滚到恢复前状态。", record.RestoreId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "回滚未完成恢复 {RestoreId} 失败，备份目录保留。", record.RestoreId);
            throw;
        }
    }

    /// <summary>按 Journal/备份目录回滚：恢复所有被覆盖/删除的文件，删除新增文件。</summary>
    private async Task RollbackAsync(
        string savePath, string backupDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(backupDir))
            return;

        // 备份目录 = 恢复前完整目录内容。回滚 = 清空当前目录后恢复备份。
        if (Directory.Exists(savePath))
        {
            foreach (var file in SafeEnumerateFiles(savePath))
            {
                File.Delete(file);
            }
        }

        foreach (var file in SafeEnumerateFiles(backupDir))
        {
            var relative = Path.GetRelativePath(backupDir, file);
            var target = Path.Combine(savePath, relative);
            var targetDir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetDir);
            File.Move(file, target, overwrite: true);
            await Task.Yield();
        }

        CleanupDir(backupDir);
    }

    private async Task BuildStagingAsync(
        Manifest manifest, string stagingDir, CancellationToken cancellationToken)
    {
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(stagingDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(dir);
            await using (var source = await _objectStore.OpenReadAsync(entry.Sha256, cancellationToken)
                .ConfigureAwait(false))
            await using (var output = File.Create(targetPath))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task BackupCurrentDirectoryAsync(
        string savePath, string backupDir, List<JournalEntry> journal, string journalPath,
        CancellationToken cancellationToken)
    {
        foreach (var file in SafeEnumerateFiles(savePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(savePath, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var backupPath = Path.Combine(backupDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(file, backupPath, overwrite: true);
            journal.Add(new JournalEntry { Op = "backup", Target = relative, Backup = relative });
            await WriteJournalAsync(journal, journalPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReplaceFilesAsync(
        Manifest manifest, string stagingDir, string savePath, string backupDir,
        List<JournalEntry> journal, string journalPath, CancellationToken cancellationToken)
    {
        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 写入目标文件
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifestPaths.Add(entry.Path);
            var sourcePath = Path.Combine(stagingDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var targetPath = Path.Combine(savePath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            journal.Add(new JournalEntry
            {
                Op = "write",
                Target = entry.Path,
                Backup = File.Exists(Path.Combine(backupDir, entry.Path.Replace('/', Path.DirectorySeparatorChar)))
                    ? entry.Path
                    : null,
            });
            await WriteJournalAsync(journal, journalPath, cancellationToken).ConfigureAwait(false);
        }

        // 删除多余文件
        foreach (var file in SafeEnumerateFiles(savePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(savePath, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var normalized = PathNormalizer.TryNormalize(relative);
            if (normalized is not null && manifestPaths.Contains(normalized))
                continue;
            File.Delete(file);
            journal.Add(new JournalEntry { Op = "delete", Target = relative, Backup = relative });
            await WriteJournalAsync(journal, journalPath, cancellationToken).ConfigureAwait(false);
        }

        // 设置修改时间与 Manifest 一致，便于后续本地状态比对
        foreach (var entry in manifest.Files)
        {
            if (entry.LastWriteUtc is null)
                continue;
            if (!UtcClock.TryParse(entry.LastWriteUtc, out var mtime))
                continue;
            var targetPath = Path.Combine(savePath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(targetPath))
                File.SetLastWriteTimeUtc(targetPath, mtime.UtcDateTime);
        }
    }

    private async Task VerifyDirectoryAsync(
        Manifest manifest, string savePath, CancellationToken cancellationToken)
    {
        var scanner = new FileScanner();
        var scanned = scanner.Scan(savePath, cancellationToken);
        var actual = scanned.ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);
        var expected = manifest.Files.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);

        if (actual.Count != expected.Count)
            throw new RepoIntegrityException(
                $"恢复后文件数量不一致：期望 {expected.Count}，实际 {actual.Count}。");

        foreach (var (path, entry) in expected)
        {
            if (!actual.TryGetValue(path, out var found))
                throw new RepoIntegrityException($"恢复后缺少文件: {path}");
            if (found.Size != entry.Size)
                throw new RepoIntegrityException($"恢复后文件大小不一致: {path}");

            var sha = await FileHasher.ComputeSha256Async(found.FullPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(sha, entry.Sha256, StringComparison.Ordinal))
                throw new RepoIntegrityException($"恢复后文件哈希不一致: {path}");
        }

        await Task.CompletedTask;
    }

    private async Task<CommitInfo> GetCommitAsync(
        string gameId, string commitId, CancellationToken cancellationToken)
    {
        var commits = await _repoFiles.ListCommitsAsync(gameId, cancellationToken).ConfigureAwait(false);
        return commits.FirstOrDefault(c => string.Equals(c.CommitId, commitId, StringComparison.OrdinalIgnoreCase))
            ?? throw new RepoIntegrityException($"Commit {commitId} 不存在于仓库（game {gameId}）。");
    }

    private static async Task WriteJournalAsync(
        List<JournalEntry> journal, string journalPath, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalOptions);
        var tempPath = journalPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, journalPath, overwrite: true);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", options)
            : Enumerable.Empty<string>();
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
