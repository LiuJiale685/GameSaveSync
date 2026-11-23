namespace GameSaveSync.Core.Models;

/// <summary>
/// 同步状态（方案 5.4）。注意区分“本地写入完成”与“云端上传成功”。
/// </summary>
public enum SyncStatus
{
    /// <summary>本地快照构建完成。</summary>
    LocalSnapshotReady = 0,

    /// <summary>已写入同步目录，云端上传状态未知。</summary>
    WaitingForCloudSync = 1,

    /// <summary>其他设备已发现该版本。</summary>
    RemoteVersionDiscovered = 2,

    /// <summary>其他设备已取得并校验全部依赖文件。</summary>
    RemoteVersionVerified = 3,

    /// <summary>发现并行版本或本地未提交修改。</summary>
    ConflictDetected = 4,

    /// <summary>Commit/Manifest/Object 尚未全部同步到本机。</summary>
    WaitingForFiles = 5,

    /// <summary>已确认恢复目标，等待安全恢复。</summary>
    RestorePending = 6,

    /// <summary>操作失败，需要重试或用户介入。</summary>
    Failed = 7,
}

/// <summary>同步任务类型。</summary>
public enum SyncTaskType
{
    /// <summary>从游戏目录构建快照并发布 Commit。</summary>
    Backup = 0,

    /// <summary>应用远端/历史版本到本地游戏目录。</summary>
    ApplyVersion = 1,

    /// <summary>恢复指定历史版本。</summary>
    Restore = 2,

    /// <summary>仓库扫描。</summary>
    RepoScan = 3,
}

/// <summary>冲突处理选项（方案 7.4）。</summary>
public enum ConflictResolution
{
    /// <summary>保留本机版本：将本机当前存档创建为新 Commit，保留远端并行版本。</summary>
    KeepLocal = 0,

    /// <summary>使用云端版本：先快照备份本机当前存档，再应用选定的远端版本。</summary>
    UseRemote = 1,

    /// <summary>分别保留两个版本到临时目录，由用户检查后选择。</summary>
    KeepBothToTemp = 2,
}
