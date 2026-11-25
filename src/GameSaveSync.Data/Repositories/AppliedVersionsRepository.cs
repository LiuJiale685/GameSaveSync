// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Data.Repositories;

/// <summary>
/// 本机已应用版本记录（方案 7.3：lastAppliedCommitId / lastAppliedManifestHash）。
/// </summary>
public sealed class AppliedVersionsRepository
{
    private readonly SqliteDatabase _db;

    public AppliedVersionsRepository(SqliteDatabase db) => _db = db;

    public Task SetAsync(string gameId, string commitId, string manifestHash, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AppliedVersions (GameId, LastAppliedCommitId, ManifestHash, AppliedAt)
                VALUES ($g, $c, $m, $t)
                ON CONFLICT(GameId) DO UPDATE SET
                    LastAppliedCommitId = excluded.LastAppliedCommitId,
                    ManifestHash = excluded.ManifestHash,
                    AppliedAt = excluded.AppliedAt
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$c", commitId);
            cmd.Parameters.AddWithValue("$m", manifestHash);
            cmd.Parameters.AddWithValue("$t", UtcClock.Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<AppliedVersionRecord?> GetAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LastAppliedCommitId, ManifestHash, AppliedAt FROM AppliedVersions WHERE GameId = $g";
            cmd.Parameters.AddWithValue("$g", gameId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new AppliedVersionRecord(gameId, reader.GetString(0), reader.GetString(1), reader.GetString(2))
                : null;
        }, ct);

    public Task RemoveAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM AppliedVersions WHERE GameId = $g";
            cmd.Parameters.AddWithValue("$g", gameId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
}

public sealed record AppliedVersionRecord(string GameId, string CommitId, string ManifestHash, string AppliedAt);

/// <summary>
/// 已发现提交缓存（方案 4.2 Commits 表）。
/// </summary>
public sealed class CommitCacheRepository
{
    private readonly SqliteDatabase _db;

    public CommitCacheRepository(SqliteDatabase db) => _db = db;

    public Task UpsertAsync(CommitInfo commit, bool localOrigin, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Commits (CommitId, GameId, DeviceId, ManifestHash, ParentIds, CreatedAtUtc, Message, LocalOrigin)
                VALUES ($cid, $g, $d, $m, $p, $t, $msg, $lo)
                ON CONFLICT(CommitId) DO UPDATE SET
                    ManifestHash = excluded.ManifestHash,
                    ParentIds = excluded.ParentIds,
                    Message = excluded.Message
                """;
            cmd.Parameters.AddWithValue("$cid", commit.CommitId);
            cmd.Parameters.AddWithValue("$g", commit.GameId);
            cmd.Parameters.AddWithValue("$d", commit.DeviceId);
            cmd.Parameters.AddWithValue("$m", commit.ManifestHash);
            cmd.Parameters.AddWithValue("$p", string.Join(';', commit.ParentIds));
            cmd.Parameters.AddWithValue("$t", commit.CreatedAtUtc);
            cmd.Parameters.AddWithValue("$msg", (object?)commit.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lo", localOrigin ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyList<CommitInfo>> ListByGameAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<CommitInfo>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT CommitId, GameId, DeviceId, ParentIds, ManifestHash, CreatedAtUtc, Message FROM Commits WHERE GameId = $g";
            cmd.Parameters.AddWithValue("$g", gameId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new CommitInfo
                {
                    CommitId = reader.GetString(0),
                    GameId = reader.GetString(1),
                    DeviceId = reader.GetString(2),
                    ParentIds = reader.GetString(3)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    ManifestHash = reader.GetString(4),
                    CreatedAtUtc = reader.GetString(5),
                    Message = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
            return (IReadOnlyList<CommitInfo>)list;
        }, ct);
}
