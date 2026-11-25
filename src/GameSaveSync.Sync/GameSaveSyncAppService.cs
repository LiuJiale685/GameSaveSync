// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Interfaces;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Data;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Snapshot;
using GameSaveSync.Storage;
using GameSaveSync.Sync.ConflictResolver;
using GameSaveSync.Sync.RestoreManager;
using GameSaveSync.Sync.SyncEngine;
using Serilog;
using ConflictResolverEngine = GameSaveSync.Sync.ConflictResolver.ConflictResolver;
using ConflictResolutionResult = GameSaveSync.Sync.ConflictResolver.ConflictResolver.ResolutionResult;
using CommitManagerEngine = GameSaveSync.Sync.CommitManager.CommitManager;
using RestoreManagerEngine = GameSaveSync.Sync.RestoreManager.RestoreManager;
using SyncEngineEngine = GameSaveSync.Sync.SyncEngine.SyncEngine;

namespace GameSaveSync.Sync;

/// <summary>
/// 应用门面服务：组合存储适配、快照、同步、冲突、恢复各引擎，
/// 供 Desktop（或未来其他 UI）调用。核心业务逻辑不依赖 WPF（方案 10.2）。
/// </summary>
public sealed class GameSaveSyncAppService : IAsyncDisposable
{
    private readonly SqliteDatabase _db;
    private readonly DeviceRepository _deviceRepo;
    private readonly GameRepository _gameRepo;
    private readonly AppSettingsRepository _settings;
    private readonly AppliedVersionsRepository _applied;
    private readonly CommitCacheRepository _commitCache;
    private readonly ConflictRepository _conflictRepo;
    private readonly RestoreJournalRepository _journalRepo;
    private readonly PlaySessionRepository _playSessions;
    private readonly IGameProcessChecker _processChecker;

    private FolderRepositoryStore? _store;
    private RepoFileService? _repoFiles;
    private ObjectStore? _objectStore;
    private SnapshotBuilder? _snapshotBuilder;
    private CommitManagerEngine? _commitManager;
    private RestoreManagerEngine? _restoreManager;
    private SyncEngineEngine? _syncEngine;
    private ConflictResolverEngine? _conflictResolver;
    private DeviceInfoRecord? _device;
    private string? _repoPath;

    public GameSaveSyncAppService(
        SqliteDatabase db,
        IGameProcessChecker processChecker)
    {
        _db = db;
        _processChecker = processChecker;
        _deviceRepo = new DeviceRepository(db);
        _gameRepo = new GameRepository(db);
        _settings = new AppSettingsRepository(db);
        _applied = new AppliedVersionsRepository(db);
        _commitCache = new CommitCacheRepository(db);
        _conflictRepo = new ConflictRepository(db);
        _journalRepo = new RestoreJournalRepository(db);
        _playSessions = new PlaySessionRepository(db);
    }

    /// <summary>配置的仓库根目录（百度网盘同步目录下的 GameSaveVault 仓库路径）。</summary>
    public string? RepoPath => _repoPath;

    public DeviceInfoRecord Device => _device
        ?? throw new InvalidOperationException("尚未初始化，请先调用 InitializeAsync。");

    /// <summary>
    /// 初始化：读取/创建本地数据库、设备身份、仓库。
    /// </summary>
    /// <param name="repoRootPath">仓库根路径（同步目录内的 GameSaveVault 目录）。</param>
    public async Task InitializeAsync(string? repoRootPath, CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        _device = await _deviceRepo.GetOrCreateDeviceAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(repoRootPath))
            await _settings.SetAsync(AppSettingsRepository.RepoPathKey, repoRootPath, ct).ConfigureAwait(false);
        else
            repoRootPath = await _settings.GetAsync(AppSettingsRepository.RepoPathKey, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(repoRootPath))
        {
            await BindRepositoryAsync(repoRootPath!, ct).ConfigureAwait(false);
        }
    }

    /// <summary>绑定/切换仓库目录（方案 3.2：读取 repo.json 确认同一仓库；T19 校验 schema）。</summary>
    public async Task BindRepositoryAsync(string repoRootPath, CancellationToken ct = default)
    {
        var store = new FolderRepositoryStore(repoRootPath);
        var repoFiles = new RepoFileService(store);

        var repo = await repoFiles.TryReadRepoInfoAsync(ct).ConfigureAwait(false);
        if (repo is null)
        {
            // 仓库不存在：初始化（repo.json create-if-absent）
            repo = await repoFiles.InitializeRepoAsync(ct).ConfigureAwait(false);
        }

        // 设备注册（create-if-absent）
        await repoFiles.RegisterDeviceAsync(new DeviceRegistration
        {
            DeviceId = Device.DeviceId,
            DeviceName = Device.DeviceName,
            CreatedAtUtc = Device.CreatedAt,
        }, ct).ConfigureAwait(false);

        _repoPath = store.RootPath;
        _store = store;
        _repoFiles = repoFiles;
        _objectStore = new ObjectStore(store);
        _snapshotBuilder = new SnapshotBuilder(store);
        _commitManager = new CommitManagerEngine(store, repoFiles, _snapshotBuilder);
        _restoreManager = new RestoreManagerEngine(repoFiles, _snapshotBuilder, _objectStore, _journalRepo);
        _syncEngine = new SyncEngineEngine(
            repoFiles, _snapshotBuilder, _restoreManager, _processChecker,
            Device, _gameRepo, _applied, _commitCache, _conflictRepo);
        _conflictResolver = new ConflictResolverEngine(
            _commitManager, _restoreManager, repoFiles, _objectStore, _conflictRepo, _applied);

        Log.Information("仓库已绑定: {RepoPath}（repositoryId={RepositoryId}）", _repoPath, repo.RepositoryId);
    }

    /// <summary>是否已绑定仓库。</summary>
    public bool IsRepositoryBound => _repoFiles is not null && _repoPath is not null;

    // ---------- 游戏管理 ----------

    /// <summary>新建游戏（生成新 gameId 并创建共享 game.json，方案 4.3.1 第 4 步）。</summary>
    public async Task<LocalGameConfig> CreateNewGameAsync(
        string name, string savePath,
        IReadOnlyList<string>? processNames = null,
        string? executablePath = null,
        CancellationToken ct = default)
    {
        EnsureRepoBound();
        var gameId = Guid.NewGuid().ToString("D");
        await _repoFiles!.EnsureGameAsync(gameId, name, null, ct).ConfigureAwait(false);
        var normalizedProcessNames = ProcessNameNormalizer.NormalizeAll(processNames ?? Array.Empty<string>());

        var config = new LocalGameConfig
        {
            GameId = gameId,
            Name = name,
            SavePath = savePath,
            ProcessNames = normalizedProcessNames,
            ExecutablePath = executablePath ?? string.Empty,
        };
        await _gameRepo.AddOrUpdateGameAsync(config, ct).ConfigureAwait(false);
        await _gameRepo.SetSavePathAsync(gameId, Device.DeviceId, savePath, ct).ConfigureAwait(false);
        await PublishSharedProcessNamesAsync(gameId, normalizedProcessNames, ct).ConfigureAwait(false);
        return config;
    }

    /// <summary>列出仓库中已有的共享游戏（设备绑定已有游戏，方案 4.3.1）。</summary>
    public Task<IReadOnlyList<SharedGameInfo>> ListRepoGamesAsync(CancellationToken ct = default)
    {
        EnsureRepoBound();
        return _repoFiles!.ListGamesAsync(ct);
    }

    /// <summary>从仓库绑定已有游戏：复用共享 gameId（T17），映射本机 SavePath。</summary>
    public async Task<LocalGameConfig> BindExistingGameAsync(
        string sharedGameId, string localName, string savePath,
        IReadOnlyList<string>? processNames = null,
        string? executablePath = null,
        CancellationToken ct = default)
    {
        EnsureRepoBound();
        var shared = await _repoFiles!.ReadGameAsync(sharedGameId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"仓库中不存在游戏 {sharedGameId}。");

        // 进程名优先取用户输入；未填写时沿用仓库中的共享设置（跨设备同步）。
        var normalizedProcessNames = ProcessNameNormalizer.NormalizeAll(
            processNames is { Count: > 0 }
                ? processNames
                : await GetSharedProcessNamesAsync(sharedGameId, ct).ConfigureAwait(false));

        var config = new LocalGameConfig
        {
            GameId = sharedGameId,
            Name = localName.Length > 0 ? localName : shared.Name,
            SavePath = savePath,
            ProcessNames = normalizedProcessNames,
            ExecutablePath = executablePath ?? string.Empty,
        };
        await _gameRepo.AddOrUpdateGameAsync(config, ct).ConfigureAwait(false);
        await _gameRepo.SetSavePathAsync(sharedGameId, Device.DeviceId, savePath, ct).ConfigureAwait(false);
        return config;
    }

    public Task<IReadOnlyList<LocalGameConfig>> ListGamesAsync(CancellationToken ct = default)
        => _gameRepo.ListGamesAsync(ct);

    /// <summary>列出本机游戏（含本设备存档路径，并合并仓库共享进程名，用于游戏列表显示与编辑）。</summary>
    public async Task<IReadOnlyList<LocalGameConfig>> ListGamesWithSavePathAsync(CancellationToken ct = default)
    {
        var games = await _gameRepo.ListGamesWithSavePathAsync(Device.DeviceId, ct).ConfigureAwait(false);
        if (_repoFiles is null)
            return games;

        // 进程名以仓库共享设置为准（跨设备同步）；无共享设置时回退本机配置并补全 .exe。
        foreach (var game in games)
        {
            var shared = await _repoFiles.ReadGameSettingsAsync(game.GameId, ct).ConfigureAwait(false);
            var sharedNames = shared is null
                ? new List<string>()
                : ProcessNameNormalizer.NormalizeAll(shared.ProcessNames);

            var effective = sharedNames.Count > 0
                ? sharedNames
                : ProcessNameNormalizer.NormalizeAll(game.ProcessNames);
            if (effective.Count == 0 || effective.SequenceEqual(game.ProcessNames))
                continue;

            game.ProcessNames = effective;
            await _gameRepo.AddOrUpdateGameAsync(game, ct).ConfigureAwait(false);
        }
        return games;
    }

    /// <summary>更新本机游戏配置（名称、存档路径、进程名、可执行文件、自动化策略），并发布共享进程名。</summary>
    public async Task UpdateGameAsync(LocalGameConfig config, CancellationToken ct = default)
    {
        var normalized = ProcessNameNormalizer.NormalizeAll(config.ProcessNames);
        config.ProcessNames = normalized;

        await _gameRepo.AddOrUpdateGameAsync(config, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(config.SavePath))
            await _gameRepo.SetSavePathAsync(config.GameId, Device.DeviceId, config.SavePath, ct)
                .ConfigureAwait(false);
        await PublishSharedProcessNamesAsync(config.GameId, normalized, ct).ConfigureAwait(false);
    }

    /// <summary>读取仓库中的共享进程名（不存在或未配置返回空列表）。</summary>
    public async Task<IReadOnlyList<string>> GetSharedProcessNamesAsync(
        string gameId, CancellationToken ct = default)
    {
        if (_repoFiles is null)
            return Array.Empty<string>();
        var settings = await _repoFiles.ReadGameSettingsAsync(gameId, ct).ConfigureAwait(false);
        return settings is null
            ? Array.Empty<string>()
            : ProcessNameNormalizer.NormalizeAll(settings.ProcessNames);
    }

    /// <summary>把进程名发布到仓库共享设置（跨设备同步，后写者胜）。</summary>
    private async Task PublishSharedProcessNamesAsync(
        string gameId, IReadOnlyList<string> processNames, CancellationToken ct)
    {
        if (_repoFiles is null)
            return;
        await _repoFiles.WriteGameSettingsAsync(new SharedGameSettings
        {
            GameId = gameId,
            ProcessNames = processNames.ToList(),
            UpdatedAtUtc = UtcClock.Now(),
            UpdatedByDeviceId = Device.DeviceId,
        }, ct).ConfigureAwait(false);
    }

    public Task RemoveGameAsync(string gameId, CancellationToken ct = default)
        => _gameRepo.RemoveGameAsync(gameId, ct);

    /// <summary>
    /// 设置游戏是否在列表中隐藏（仅影响显示，存档、版本历史与自动备份不受影响）。
    /// </summary>
    public async Task SetGameHiddenAsync(string gameId, bool hidden, CancellationToken ct = default)
    {
        var game = await _gameRepo.GetGameAsync(gameId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"本地配置中不存在游戏 {gameId}。");
        if (game.IsHidden == hidden)
            return;

        game.IsHidden = hidden;
        await _gameRepo.AddOrUpdateGameAsync(game, ct).ConfigureAwait(false);
    }

    // ---------- 备份与版本 ----------

    /// <summary>手动为游戏创建快照并发布 Commit（阶段一核心能力）。</summary>
    public async Task<CommitInfo> BackupNowAsync(
        string gameId, string message = "手动备份", CancellationToken ct = default)
    {
        EnsureRepoBound();
        var (savePath, processNames) = await RequireGamePathsAsync(gameId, ct).ConfigureAwait(false);
        var applied = await _applied.GetAsync(gameId, ct).ConfigureAwait(false);
        var parentIds = applied is null
            ? Array.Empty<string>()
            : new[] { applied.CommitId };

        var commit = await _commitManager!.CreateCommitAsync(
            gameId, Device.DeviceId, savePath, parentIds, message, ct).ConfigureAwait(false);
        await _applied.SetAsync(gameId, commit.CommitId, commit.ManifestHash, ct).ConfigureAwait(false);
        await _commitCache.UpsertAsync(commit, localOrigin: true, ct).ConfigureAwait(false);
        return commit;
    }

    /// <summary>列出游戏的版本历史（从仓库扫描全部 Commit）。</summary>
    public Task<IReadOnlyList<CommitInfo>> ListVersionsAsync(
        string gameId, CancellationToken ct = default)
    {
        EnsureRepoBound();
        return _repoFiles!.ListCommitsAsync(gameId, ct);
    }

    /// <summary>读取版本文件清单（版本浏览）。</summary>
    public Task<Manifest?> GetManifestAsync(
        string gameId, string manifestHash, CancellationToken ct = default)
    {
        EnsureRepoBound();
        return _repoFiles!.TryReadManifestAsync(gameId, manifestHash, ct);
    }

    // ---------- 同步与冲突 ----------

    /// <summary>扫描游戏同步状态（方案 7.3 决策表）。</summary>
    public async Task<SyncEngineEngine.GameSyncReport> ScanGameAsync(
        string gameId, CancellationToken ct = default)
    {
        EnsureRepoBound();
        var (savePath, processNames) = await RequireGamePathsAsync(gameId, ct).ConfigureAwait(false);
        return await _syncEngine!.ScanGameAsync(gameId, savePath, processNames, ct).ConfigureAwait(false);
    }

    /// <summary>自动应用远端版本（须满足 ScanGameAsync 给出的 AutoApplyCandidate 条件）。</summary>
    public async Task ApplyRemoteVersionAsync(
        string gameId, string commitId, CancellationToken ct = default)
    {
        EnsureRepoBound();
        var savePath = await GetSavePath(gameId, ct).ConfigureAwait(false);
        await _syncEngine!.ApplyRemoteVersionAsync(gameId, commitId, savePath, ct).ConfigureAwait(false);
    }

    /// <summary>处理冲突（方案 7.4）。</summary>
    public async Task<ConflictResolutionResult> ResolveConflictAsync(
        string conflictId, ConflictResolution resolution, string? selectedRemoteCommitId,
        CancellationToken ct = default)
    {
        EnsureRepoBound();
        var conflicts = await _conflictRepo.ListOpenAsync(ct).ConfigureAwait(false);
        var conflict = conflicts.FirstOrDefault(c => c.ConflictId == conflictId)
            ?? throw new InvalidOperationException($"冲突记录 {conflictId} 不存在或已处理。");
        var savePath = await GetSavePath(conflict.GameId, ct).ConfigureAwait(false);
        return await _conflictResolver!.ResolveAsync(
            conflictId, resolution, selectedRemoteCommitId,
            conflict.GameId, Device.DeviceId, savePath, ct).ConfigureAwait(false);
    }

    /// <summary>列出未处理冲突。</summary>
    public Task<IReadOnlyList<ConflictRecord>> ListConflictsAsync(CancellationToken ct = default)
        => _conflictRepo.ListOpenAsync(ct);

    // ---------- 恢复 ----------

    /// <summary>
    /// 恢复历史版本（方案 8.1）：先对当前目录发布备份 Commit，再安全应用目标版本，
    /// 最后创建恢复 Commit（回滚也是新版本，8.3）。
    /// </summary>
    public async Task<RestoreManagerEngine.ApplyResult> RestoreVersionAsync(
        string gameId, string targetCommitId, CancellationToken ct = default)
    {
        EnsureRepoBound();
        var savePath = await GetSavePath(gameId, ct).ConfigureAwait(false);
        var applied = await _applied.GetAsync(gameId, ct).ConfigureAwait(false);

        // 步骤 6：恢复前备份当前目录并发布为 Commit
        var backupParents = applied is null
            ? Array.Empty<string>()
            : new[] { applied.CommitId };
        var backupCommit = await _commitManager!.CreateCommitAsync(
            gameId, Device.DeviceId, savePath, backupParents,
            "恢复前自动备份", ct).ConfigureAwait(false);

        // 步骤 5-8：安全应用目标版本
        var result = await _restoreManager!.ApplyCommitAsync(gameId, targetCommitId, savePath, ct)
            .ConfigureAwait(false);
        if (!result.Success)
            return result;

        // 步骤 9：创建恢复 Commit，父版本同时指向备份 Commit 与被恢复的目标版本，
        // 使恢复后的提交成为目标版本的直接后代（收敛版本关系），
        // 避免目标版本成为并行 Head 而在下次扫描时误报冲突。
        var target = await FindCommitAsync(gameId, targetCommitId, ct).ConfigureAwait(false);
        var restoreCommit = await _commitManager.CreateCommitReferencingExistingManifestAsync(
            gameId, Device.DeviceId, target.ManifestHash,
            new[] { backupCommit.CommitId, target.CommitId }.Distinct().ToList(),
            "恢复历史版本", ct).ConfigureAwait(false);

        await _applied.SetAsync(gameId, restoreCommit.CommitId, restoreCommit.ManifestHash, ct)
            .ConfigureAwait(false);

        Log.Information(
            "恢复完成：game {GameId}，目标 {Target}，恢复前备份 {Backup}，恢复 Commit {Restore}",
            gameId, targetCommitId[..8], backupCommit.CommitId[..8], restoreCommit.CommitId[..8]);
        return result;
    }

    /// <summary>启动时恢复未完成任务：崩溃残留恢复回滚（T14）。</summary>
    public async Task RecoverPendingRestoresAsync(CancellationToken ct = default)
    {
        var pending = await _journalRepo.ListPendingAsync(ct).ConfigureAwait(false);
        foreach (var record in pending)
        {
            try
            {
                var savePath = await GetSavePath(record.GameId, ct).ConfigureAwait(false);
                await _restoreManager!.RollbackPendingRestoreAsync(record, savePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "未完成恢复 {RestoreId} 无法自动回滚，需人工处理。", record.RestoreId);
            }
        }
    }

    /// <summary>启动时重置未完成同步任务（T11：Running 残留重置为 Pending）。</summary>
    public Task<IReadOnlyList<SyncTaskRecord>> RecoverPendingTasksAsync(CancellationToken ct = default)
        => new SyncTaskRepository(_db).ListIncompleteAsync(ct);

    // ---------- 仓库迁移 ----------

    /// <summary>
    /// 仓库迁移：把当前仓库的全部内容（repo.json、devices、games、commits、manifests、objects）
    /// 复制到新目录，校验后自动切换绑定并更新本地设置。更换网盘同步目录时使用。
    /// 保留原 repositoryId，因此历史版本、其他设备的 Commit 均继续有效。
    /// </summary>
    /// <param name="newRepoRootPath">新仓库根目录（不存在则创建；已存在则须为空目录或同一仓库）。</param>
    /// <param name="clearSourceRepository">迁移成功后是否删除旧目录中的仓库文件。</param>
    public async Task<RepoMigrationResult> MigrateRepositoryAsync(
        string newRepoRootPath,
        bool clearSourceRepository = false,
        IProgress<RepoMigrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        EnsureRepoBound();
        if (string.IsNullOrWhiteSpace(newRepoRootPath))
            throw new ArgumentException("新仓库目录不能为空。", nameof(newRepoRootPath));

        var sourceRoot = Path.GetFullPath(_repoPath!);
        var targetRoot = Path.GetFullPath(newRepoRootPath.Trim());

        if (PathNormalizer.PathsEqual(sourceRoot, targetRoot))
            throw new InvalidOperationException("新仓库目录与当前仓库相同，无需迁移。");
        if (PathNormalizer.IsUnderRoot(targetRoot, sourceRoot) || PathNormalizer.IsUnderRoot(sourceRoot, targetRoot))
            throw new InvalidOperationException("新仓库目录不能位于当前仓库目录之内，也不能包含当前仓库目录。");

        var sourceRepo = await _repoFiles!.TryReadRepoInfoAsync(ct).ConfigureAwait(false)
            ?? throw new RepoIntegrityException("当前仓库缺少 repo.json，无法迁移。");

        // 目标目录校验：不存在则创建；已存在则必须是空目录，或同一个仓库（补齐缺失内容）。
        if (!Directory.Exists(targetRoot))
        {
            Directory.CreateDirectory(targetRoot);
        }
        else if (!File.Exists(Path.Combine(targetRoot, RepoPaths.RepoInfoKey))
                 && Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            throw new InvalidOperationException($"目标目录非空且不是 GameSaveSync 仓库：{targetRoot}");
        }

        var targetRepoFiles = new RepoFileService(new FolderRepositoryStore(targetRoot));
        var targetRepo = await targetRepoFiles.TryReadRepoInfoAsync(ct).ConfigureAwait(false);
        if (targetRepo is not null
            && !string.Equals(targetRepo.RepositoryId, sourceRepo.RepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"目标目录已存放另一个仓库（repositoryId={targetRepo.RepositoryId}），" +
                "为避免两个仓库的数据混在一起，迁移已取消。请选择一个空目录或同一个仓库目录。");
        }

        // 逐文件复制并校验：已存在且内容一致则跳过，不一致直接报错而非覆盖。
        var sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(f => !IsTempArtifact(f))
            .ToList();
        var totalBytes = sourceFiles.Sum(f => new FileInfo(f).Length);
        var copiedFiles = 0;
        long copiedBytes = 0;

        progress?.Report(new RepoMigrationProgress(0, sourceFiles.Count, 0, totalBytes));
        foreach (var sourceFile in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var targetFile = Path.Combine(targetRoot, relative);
            copiedBytes += await CopyFileVerifiedAsync(sourceFile, targetFile, ct).ConfigureAwait(false);
            copiedFiles++;
            if (copiedFiles % 16 == 0 || copiedFiles == sourceFiles.Count)
                progress?.Report(new RepoMigrationProgress(copiedFiles, sourceFiles.Count, copiedBytes, totalBytes));
        }

        // 复制完成后再确认一次仓库身份，然后切换绑定。
        var migratedRepo = await targetRepoFiles.TryReadRepoInfoAsync(ct).ConfigureAwait(false)
            ?? throw new RepoIntegrityException($"迁移后目标目录缺少 repo.json：{targetRoot}");
        if (!string.Equals(migratedRepo.RepositoryId, sourceRepo.RepositoryId, StringComparison.OrdinalIgnoreCase))
            throw new RepoIntegrityException("迁移后目标仓库的 repositoryId 与源仓库不一致。");

        await BindRepositoryAsync(targetRoot, ct).ConfigureAwait(false);
        await _settings.SetAsync(AppSettingsRepository.RepoPathKey, targetRoot, ct).ConfigureAwait(false);

        var sourceCleared = false;
        string? cleanupError = null;
        if (clearSourceRepository)
        {
            try
            {
                Directory.Delete(sourceRoot, recursive: true);
                sourceCleared = true;
            }
            catch (Exception ex)
            {
                cleanupError = ex.Message;
                Log.Warning(ex, "迁移完成但旧目录清理失败: {SourceRoot}", sourceRoot);
            }
        }

        Log.Information(
            "仓库迁移完成：{Source} → {Target}（{FileCount} 个文件 / {Bytes} 字节，repositoryId={RepositoryId}，旧目录已清理={Cleared}）",
            sourceRoot, targetRoot, copiedFiles, copiedBytes, migratedRepo.RepositoryId, sourceCleared);

        return new RepoMigrationResult(
            targetRoot, migratedRepo.RepositoryId, copiedFiles, copiedBytes, sourceCleared, cleanupError);
    }

    /// <summary>复制单个仓库文件：写入临时文件 → 双向哈希校验 → 原子改名；已存在且一致则跳过。</summary>
    private static async Task<long> CopyFileVerifiedAsync(
        string sourcePath, string targetPath, CancellationToken ct)
    {
        var sourceLength = new FileInfo(sourcePath).Length;

        if (File.Exists(targetPath))
        {
            if (new FileInfo(targetPath).Length == sourceLength)
            {
                var sourceHash = await ComputeFileSha256Async(sourcePath, ct).ConfigureAwait(false);
                var targetHash = await ComputeFileSha256Async(targetPath, ct).ConfigureAwait(false);
                if (string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
                    return sourceLength;
            }
            throw new RepoIntegrityException($"目标仓库已存在内容不同的同名文件：{targetPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = File.Create(tempPath))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            var sourceHash = await ComputeFileSha256Async(sourcePath, ct).ConfigureAwait(false);
            var tempHash = await ComputeFileSha256Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(sourceHash, tempHash, StringComparison.Ordinal))
                throw new RepoIntegrityException($"复制校验失败：{sourcePath}");

            File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { }
            }
        }

        return sourceLength;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await FolderRepositoryStore.ComputeSha256Async(stream, ct).ConfigureAwait(false);
    }

    /// <summary>仓库临时文件（写入中断残留的 .*.tmp）不参与迁移。</summary>
    private static bool IsTempArtifact(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 游戏时长统计 ----------

    /// <summary>某游戏的时长汇总（总时长、会话数、平均/最长、首次/最后游戏时间）。</summary>
    public Task<PlaySessionStats> GetGamePlayStatsAsync(
        string gameId, CancellationToken ct = default)
        => _playSessions.GetStatsAsync(gameId, ct);

    /// <summary>某游戏最近的会话明细（默认最近 10 条）。</summary>
    public Task<IReadOnlyList<PlaySessionRecord>> GetRecentPlaySessionsAsync(
        string gameId, int topN = 10, CancellationToken ct = default)
        => _playSessions.GetRecentSessionsAsync(gameId, topN, ct);

    /// <summary>导出全部会话明细为 CSV（UTF-8 带 BOM，Excel 可直接打开）。返回写入的文件路径。</summary>
    public async Task<string> ExportPlaySessionsCsvAsync(
        string filePath, CancellationToken ct = default)
    {
        var sessions = await _playSessions.GetAllSessionsAsync(ct).ConfigureAwait(false);
        var games = await _gameRepo.ListGamesAsync(ct).ConfigureAwait(false);
        var nameById = games.ToDictionary(g => g.GameId, g => g.Name, StringComparer.OrdinalIgnoreCase);

        static string Csv(string? value)
        {
            var text = value ?? string.Empty;
            return text.Contains('"') || text.Contains(',') || text.Contains('\n')
                ? '"' + text.Replace("\"", "\"\"") + '"'
                : text;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("游戏名,GameId,会话ID,开始时间,结束时间,时长(秒),结束原因");
        foreach (var s in sessions)
        {
            var gameName = nameById.TryGetValue(s.GameId, out var name) ? name : s.GameId;
            builder.AppendLine(string.Join(',',
                Csv(gameName),
                Csv(s.GameId),
                Csv(s.SessionId),
                Csv(FormatLocal(s.StartedAt)),
                Csv(FormatLocal(s.EndedAt)),
                s.DurationSeconds.ToString(),
                Csv(DescribeEndReason(s.EndReason))));
        }

        await File.WriteAllTextAsync(filePath, builder.ToString(),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);
        Log.Information("游戏时长数据已导出：{Path}（{Count} 条会话）", filePath, sessions.Count);
        return filePath;
    }

    private static string FormatLocal(string utc)
        => UtcClock.TryParse(utc, out var value)
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : utc;

    private static string DescribeEndReason(PlaySessionEndReason reason) => reason switch
    {
        PlaySessionEndReason.NormalExit => "正常退出",
        PlaySessionEndReason.AppClosed => "程序关闭时仍在运行",
        PlaySessionEndReason.Interrupted => "中断（异常结束）",
        _ => "未知",
    };

    // ---------- 统计 ----------

    /// <summary>仓库对象数与体积统计（方案 13.3）。</summary>
    public Task<(int Count, long TotalBytes)> GetRepoStatsAsync(CancellationToken ct = default)
    {
        EnsureRepoBound();
        return _objectStore!.GetStatsAsync(ct);
    }

    /// <summary>重新生成设备 ID（方案 7.6 / T20，环境克隆后使用）。</summary>
    public async Task<DeviceInfoRecord> RegenerateDeviceIdAsync(CancellationToken ct = default)
    {
        _device = await _deviceRepo.RegenerateDeviceAsync(ct).ConfigureAwait(false);
        if (_repoPath is not null)
        {
            await BindRepositoryAsync(_repoPath, ct).ConfigureAwait(false);
        }
        return _device;
    }

    private async Task<(string SavePath, IReadOnlyList<string> ProcessNames)> RequireGamePathsAsync(
        string gameId, CancellationToken ct)
    {
        var game = await _gameRepo.GetGameAsync(gameId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"本地配置中不存在游戏 {gameId}。");
        var savePath = await GetSavePath(gameId, ct).ConfigureAwait(false);
        return (savePath, game.ProcessNames);
    }

    private async Task<string> GetSavePath(string gameId, CancellationToken ct)
    {
        var path = await _gameRepo.GetSavePathAsync(gameId, Device.DeviceId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"游戏 {gameId} 未配置本机存档路径。");
        return path!;
    }

    private async Task<CommitInfo> FindCommitAsync(string gameId, string commitId, CancellationToken ct)
    {
        var commits = await _repoFiles!.ListCommitsAsync(gameId, ct).ConfigureAwait(false);
        return commits.FirstOrDefault(c => string.Equals(c.CommitId, commitId, StringComparison.OrdinalIgnoreCase))
            ?? throw new RepoIntegrityException($"Commit {commitId} 不存在。");
    }

    private void EnsureRepoBound()
    {
        if (_repoFiles is null || _repoPath is null)
            throw new InvalidOperationException("尚未绑定同步仓库，请先在设置中配置仓库目录。");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
    }
}

/// <summary>仓库迁移进度（用于 UI 提示）。</summary>
public readonly record struct RepoMigrationProgress(
    int CopiedFiles, int TotalFiles, long CopiedBytes, long TotalBytes);

/// <summary>仓库迁移结果。</summary>
/// <param name="NewRepoPath">迁移后的新仓库根目录（已绑定）。</param>
/// <param name="RepositoryId">仓库 ID（迁移后保持不变）。</param>
/// <param name="CopiedFileCount">已校验复制的文件数。</param>
/// <param name="CopiedBytes">已校验复制的字节数。</param>
/// <param name="SourceCleared">旧目录是否已删除。</param>
/// <param name="CleanupError">旧目录删除失败时的错误信息（为 null 表示无错误）。</param>
public sealed record RepoMigrationResult(
    string NewRepoPath,
    string RepositoryId,
    int CopiedFileCount,
    long CopiedBytes,
    bool SourceCleared,
    string? CleanupError);
