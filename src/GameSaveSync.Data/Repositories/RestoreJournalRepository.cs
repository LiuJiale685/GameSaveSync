// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Data.Repositories;

/// <summary>
/// 恢复日志断点（方案 4.2 RestoreJournals / 8.2 / T14）。
/// Journal 明细文件保存在 %LOCALAPPDATA%/GameSaveSync/journals/ 下。
/// </summary>
public sealed class RestoreJournalRepository
{
    private readonly SqliteDatabase _db;

    public RestoreJournalRepository(SqliteDatabase db) => _db = db;

    public Task AddAsync(RestoreJournalRecord record, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO RestoreJournals (RestoreId, GameId, TargetCommitId, Status, JournalPath, CreatedAt, UpdatedAt)
                VALUES ($id, $g, $t, $s, $p, $c, $u)
                """;
            cmd.Parameters.AddWithValue("$id", record.RestoreId);
            cmd.Parameters.AddWithValue("$g", record.GameId);
            cmd.Parameters.AddWithValue("$t", record.TargetCommitId);
            cmd.Parameters.AddWithValue("$s", (int)record.Status);
            cmd.Parameters.AddWithValue("$p", record.JournalPath);
            cmd.Parameters.AddWithValue("$c", record.CreatedAt);
            cmd.Parameters.AddWithValue("$u", UtcClock.Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task UpdateStatusAsync(string restoreId, RestoreJournalStatus status, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE RestoreJournals SET Status = $s, UpdatedAt = $u WHERE RestoreId = $id";
            cmd.Parameters.AddWithValue("$s", (int)status);
            cmd.Parameters.AddWithValue("$u", UtcClock.Now());
            cmd.Parameters.AddWithValue("$id", restoreId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>列出未完成（进行中）的恢复任务，供启动时恢复/回滚。</summary>
    public Task<IReadOnlyList<RestoreJournalRecord>> ListPendingAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<RestoreJournalRecord>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT RestoreId, GameId, TargetCommitId, Status, JournalPath, CreatedAt, UpdatedAt FROM RestoreJournals WHERE Status = 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new RestoreJournalRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (RestoreJournalStatus)reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
            return (IReadOnlyList<RestoreJournalRecord>)list;
        }, ct);

    public static string JournalsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "GameSaveSync", "journals");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>恢复任务状态：1 = 进行中（可能中断），2 = 已完成，3 = 已回滚，4 = 已放弃。</summary>
public enum RestoreJournalStatus
{
    InProgress = 1,
    Completed = 2,
    RolledBack = 3,
    Abandoned = 4,
}

public sealed record RestoreJournalRecord(
    string RestoreId,
    string GameId,
    string TargetCommitId,
    RestoreJournalStatus Status,
    string JournalPath,
    string CreatedAt,
    string UpdatedAt);
