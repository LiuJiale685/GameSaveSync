// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Data;

/// <summary>
/// 数据库结构（方案 4.2）。
/// </summary>
public static class Schema
{
    public const int CurrentVersion = 2;

    private static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS Meta (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS Devices (
            DeviceId TEXT PRIMARY KEY,
            DeviceName TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS Games (
            GameId TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            ProcessNames TEXT NOT NULL DEFAULT '',
            ExecutablePath TEXT NOT NULL DEFAULT '',
            AutoBackup INTEGER NOT NULL DEFAULT 1,
            AutoRestore INTEGER NOT NULL DEFAULT 1,
            Hidden INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS GamePaths (
            GameId TEXT NOT NULL,
            DeviceId TEXT NOT NULL,
            SavePath TEXT NOT NULL,
            PRIMARY KEY (GameId, DeviceId)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS Commits (
            CommitId TEXT PRIMARY KEY,
            GameId TEXT NOT NULL,
            DeviceId TEXT NOT NULL,
            ManifestHash TEXT NOT NULL,
            ParentIds TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            Message TEXT,
            LocalOrigin INTEGER NOT NULL DEFAULT 0
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_Commits_Game ON Commits(GameId)",
        """
        CREATE TABLE IF NOT EXISTS Snapshots (
            SnapshotId TEXT PRIMARY KEY,
            GameId TEXT NOT NULL,
            ManifestHash TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS FileCache (
            GameId TEXT NOT NULL,
            RelativePath TEXT NOT NULL,
            Size INTEGER NOT NULL,
            LastWriteUtc TEXT NOT NULL,
            Sha256 TEXT NOT NULL,
            PRIMARY KEY (GameId, RelativePath)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS SyncTasks (
            TaskId TEXT PRIMARY KEY,
            GameId TEXT NOT NULL,
            TaskType INTEGER NOT NULL,
            Status INTEGER NOT NULL,
            Payload TEXT,
            RetryCount INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_SyncTasks_Status ON SyncTasks(Status)",
        """
        CREATE TABLE IF NOT EXISTS AppliedVersions (
            GameId TEXT PRIMARY KEY,
            LastAppliedCommitId TEXT NOT NULL,
            ManifestHash TEXT NOT NULL,
            AppliedAt TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS Conflicts (
            ConflictId TEXT PRIMARY KEY,
            GameId TEXT NOT NULL,
            LocalCommitId TEXT,
            RemoteCommitIds TEXT NOT NULL,
            Status INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            ResolvedAt TEXT
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS RestoreJournals (
            RestoreId TEXT PRIMARY KEY,
            GameId TEXT NOT NULL,
            TargetCommitId TEXT NOT NULL,
            Status INTEGER NOT NULL,
            JournalPath TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS PlaySessions (
            SessionId TEXT PRIMARY KEY NOT NULL,
            GameId TEXT NOT NULL,
            StartedAt TEXT NOT NULL,
            EndedAt TEXT,
            LastSeenAt TEXT NOT NULL,
            EndReason INTEGER NOT NULL DEFAULT 0,
            DurationSeconds INTEGER NOT NULL DEFAULT 0,
            IsImported INTEGER NOT NULL DEFAULT 0
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_PlaySessions_Game ON PlaySessions(GameId)",
        "CREATE INDEX IF NOT EXISTS IX_PlaySessions_Started ON PlaySessions(StartedAt)",
    };

    public static async Task CreateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var statement in Statements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureColumnAsync(connection, "Games", "ExecutablePath",
            "ALTER TABLE Games ADD COLUMN ExecutablePath TEXT NOT NULL DEFAULT ''", cancellationToken)
            .ConfigureAwait(false);

        await EnsureColumnAsync(connection, "Games", "Hidden",
            "ALTER TABLE Games ADD COLUMN Hidden INTEGER NOT NULL DEFAULT 0", cancellationToken)
            .ConfigureAwait(false);

        await EnsureColumnAsync(connection, "PlaySessions", "IsImported",
            "ALTER TABLE PlaySessions ADD COLUMN IsImported INTEGER NOT NULL DEFAULT 0", cancellationToken)
            .ConfigureAwait(false);

        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "SELECT Value FROM Meta WHERE Key = 'schemaVersion'";
        var existing = await versionCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Meta (Key, Value) VALUES ('schemaVersion', $v)";
            insert.Parameters.AddWithValue("$v", CurrentVersion.ToString());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (existing.ToString() != CurrentVersion.ToString())
        {
            // 旧库升级：表结构已通过上面的 CREATE TABLE IF NOT EXISTS 幂等补齐。
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Meta SET Value = $v WHERE Key = 'schemaVersion'";
            update.Parameters.AddWithValue("$v", CurrentVersion.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>旧库增量迁移：列不存在时执行 ALTER TABLE（已存在则忽略）。</summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection connection, string table, string column, string alterSql,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col";
        check.Parameters.AddWithValue("$col", column);
        var count = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (count > 0)
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = alterSql;
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>任务状态（SyncTasks）。</summary>
public enum TaskStatusValue
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>冲突状态（Conflicts）。</summary>
public enum ConflictStatusValue
{
    Open = 0,
    Resolved = 1,
    Ignored = 2,
}

/// <summary>游戏会话结束原因（PlaySessions.EndReason）。</summary>
public enum PlaySessionEndReason
{
    /// <summary>进行中（EndedAt 为 NULL）。</summary>
    Running = 0,

    /// <summary>正常退出（监控检测到进程退出）。</summary>
    NormalExit = 1,

    /// <summary>App 退出时游戏仍在运行。</summary>
    AppClosed = 2,

    /// <summary>崩溃 / 断电中断（启动时孤儿收尾）。</summary>
    Interrupted = 3,
}
