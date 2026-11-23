using Microsoft.Data.Sqlite;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Data.Repositories;

/// <summary>
/// 同步任务表（方案 4.2 SyncTasks）：异常退出后按状态恢复未完成任务（阶段一验收）。
/// </summary>
public sealed class SyncTaskRepository
{
    private readonly SqliteDatabase _db;

    public SyncTaskRepository(SqliteDatabase db) => _db = db;

    public Task AddAsync(SyncTaskRecord task, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SyncTasks (TaskId, GameId, TaskType, Status, Payload, RetryCount, CreatedAt, UpdatedAt)
                VALUES ($id, $g, $type, $status, $payload, $retry, $created, $updated)
                ON CONFLICT(TaskId) DO UPDATE SET
                    Status = excluded.Status,
                    Payload = excluded.Payload,
                    RetryCount = excluded.RetryCount,
                    UpdatedAt = excluded.Updated
                """;
            cmd.Parameters.AddWithValue("$id", task.TaskId);
            cmd.Parameters.AddWithValue("$g", task.GameId);
            cmd.Parameters.AddWithValue("$type", (int)task.TaskType);
            cmd.Parameters.AddWithValue("$status", (int)task.Status);
            cmd.Parameters.AddWithValue("$payload", (object?)task.Payload ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$retry", task.RetryCount);
            cmd.Parameters.AddWithValue("$created", task.CreatedAt);
            cmd.Parameters.AddWithValue("$updated", UtcClock.Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task UpdateStatusAsync(string taskId, TaskStatusValue status, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE SyncTasks SET Status = $s, UpdatedAt = $u WHERE TaskId = $id";
            cmd.Parameters.AddWithValue("$s", (int)status);
            cmd.Parameters.AddWithValue("$u", UtcClock.Now());
            cmd.Parameters.AddWithValue("$id", taskId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>列出未完成任务；Running（多为崩溃残留）重置为 Pending（T11）。</summary>
    public Task<IReadOnlyList<SyncTaskRecord>> ListIncompleteAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var reset = conn.CreateCommand();
            reset.CommandText = "UPDATE SyncTasks SET Status = 0 WHERE Status = 1";
            await reset.ExecuteNonQueryAsync(ct);

            var list = new List<SyncTaskRecord>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT TaskId, GameId, TaskType, Status, Payload, RetryCount, CreatedAt FROM SyncTasks WHERE Status IN (0, 1) ORDER BY CreatedAt";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new SyncTaskRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    (SyncTaskType)reader.GetInt64(2),
                    (TaskStatusValue)reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    (int)reader.GetInt64(5),
                    reader.GetString(6)));
            }
            return (IReadOnlyList<SyncTaskRecord>)list;
        }, ct);
}

public sealed record SyncTaskRecord(
    string TaskId,
    string GameId,
    SyncTaskType TaskType,
    TaskStatusValue Status,
    string? Payload,
    int RetryCount,
    string CreatedAt);

/// <summary>
/// 冲突记录（方案 4.2 Conflicts）。
/// </summary>
public sealed class ConflictRepository
{
    private readonly SqliteDatabase _db;

    public ConflictRepository(SqliteDatabase db) => _db = db;

    public Task AddAsync(ConflictRecord record, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Conflicts (ConflictId, GameId, LocalCommitId, RemoteCommitIds, Status, CreatedAt, ResolvedAt)
                VALUES ($id, $g, $l, $r, $s, $c, $ra)
                """;
            cmd.Parameters.AddWithValue("$id", record.ConflictId);
            cmd.Parameters.AddWithValue("$g", record.GameId);
            cmd.Parameters.AddWithValue("$l", (object?)record.LocalCommitId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", string.Join(';', record.RemoteCommitIds));
            cmd.Parameters.AddWithValue("$s", (int)record.Status);
            cmd.Parameters.AddWithValue("$c", record.CreatedAt);
            cmd.Parameters.AddWithValue("$ra", (object?)record.ResolvedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task UpdateStatusAsync(string conflictId, ConflictStatusValue status, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Conflicts SET Status = $s, ResolvedAt = $ra WHERE ConflictId = $id";
            cmd.Parameters.AddWithValue("$s", (int)status);
            cmd.Parameters.AddWithValue("$ra", status == ConflictStatusValue.Open ? DBNull.Value : UtcClock.Now());
            cmd.Parameters.AddWithValue("$id", conflictId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyList<ConflictRecord>> ListOpenAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<ConflictRecord>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT ConflictId, GameId, LocalCommitId, RemoteCommitIds, Status, CreatedAt, ResolvedAt FROM Conflicts WHERE Status = 0 ORDER BY CreatedAt";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ConflictRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    (ConflictStatusValue)reader.GetInt64(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
            return (IReadOnlyList<ConflictRecord>)list;
        }, ct);
}

public sealed record ConflictRecord(
    string ConflictId,
    string GameId,
    string? LocalCommitId,
    IReadOnlyList<string> RemoteCommitIds,
    ConflictStatusValue Status,
    string CreatedAt,
    string? ResolvedAt);
