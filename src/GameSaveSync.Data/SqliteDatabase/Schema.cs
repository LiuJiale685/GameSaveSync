using Microsoft.Data.Sqlite;

namespace GameSaveSync.Data;

/// <summary>
/// 数据库结构（方案 4.2）。
/// </summary>
public static class Schema
{
    public const int CurrentVersion = 1;

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
            AutoBackup INTEGER NOT NULL DEFAULT 1,
            AutoRestore INTEGER NOT NULL DEFAULT 1,
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
