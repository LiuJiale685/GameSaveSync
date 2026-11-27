// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Data.Repositories;

/// <summary>
/// 游戏运行会话记录（本机 SQLite，不入共享仓库）。
/// 由 PlaySessionRecorder 在进程状态转换时写入，统计查询供游戏页展示。
/// 短会话阈值在查询层过滤（<see cref="ShortSessionThresholdSeconds"/>）。
/// </summary>
public sealed class PlaySessionRepository
{
    /// <summary>统计时忽略的短会话阈值（秒）：误启动 / 启动器瞬时进程。</summary>
    public const int ShortSessionThresholdSeconds = 60;

    private readonly SqliteDatabase _db;

    public PlaySessionRepository(SqliteDatabase db) => _db = db;

    /// <summary>新建一条进行中会话。</summary>
    public Task InsertAsync(
        string sessionId, string gameId, string startedAt, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO PlaySessions (SessionId, GameId, StartedAt, EndedAt, LastSeenAt, EndReason, DurationSeconds)
                VALUES ($id, $g, $s, NULL, $s, $r, 0)
                """;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$s", startedAt);
            cmd.Parameters.AddWithValue("$r", (int)PlaySessionEndReason.Running);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>回填会话结束时间与结束原因，并按 StartedAt 计算时长（负数夹紧为 0）。</summary>
    public Task FinishAsync(
        string sessionId, string endedAt, PlaySessionEndReason reason, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE PlaySessions
                SET EndedAt = $e,
                    EndReason = $r,
                    DurationSeconds = MAX(0, CAST((julianday($e) - julianday(StartedAt)) * 86400 AS INTEGER))
                WHERE SessionId = $id
                """;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$e", endedAt);
            cmd.Parameters.AddWithValue("$r", (int)reason);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>心跳：更新进行中会话的最近可见时间。</summary>
    public Task TouchLastSeenAsync(string sessionId, string lastSeenAt, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE PlaySessions SET LastSeenAt = $t WHERE SessionId = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$t", lastSeenAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>收尾崩溃残留的进行中会话：结束时间取 LastSeenAt，标记为 Interrupted。</summary>
    public Task CloseOrphansAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE PlaySessions
                SET EndedAt = LastSeenAt,
                    EndReason = $r,
                    DurationSeconds = MAX(0, CAST((julianday(LastSeenAt) - julianday(StartedAt)) * 86400 AS INTEGER))
                WHERE EndedAt IS NULL
                """;
            cmd.Parameters.AddWithValue("$r", (int)PlaySessionEndReason.Interrupted);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>收尾全部进行中会话（App 退出时调用），结束时间取当前时刻。</summary>
    public Task CloseAllAsync(string endedAt, PlaySessionEndReason reason, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE PlaySessions
                SET EndedAt = MAX($e, StartedAt),
                    EndReason = $r,
                    DurationSeconds = MAX(0, CAST((julianday(MAX($e, StartedAt)) - julianday(StartedAt)) * 86400 AS INTEGER))
                WHERE EndedAt IS NULL
                """;
            cmd.Parameters.AddWithValue("$e", endedAt);
            cmd.Parameters.AddWithValue("$r", (int)reason);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>某游戏的汇总统计（只统计已结束且不短于阈值的会话；无数据返回零值）。</summary>
    public Task<PlaySessionStats> GetStatsAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(DurationSeconds), 0),
                       COALESCE(MAX(DurationSeconds), 0),
                       MIN(StartedAt),
                       MAX(EndedAt)
                FROM PlaySessions
                WHERE GameId = $g
                  AND EndedAt IS NOT NULL
                  AND EndReason <> 0
                  AND DurationSeconds >= $min
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$min", ShortSessionThresholdSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return PlaySessionStats.Empty(gameId);

            var count = reader.GetInt64(0);
            var total = reader.GetInt64(1);
            var longest = reader.GetInt64(2);
            var first = reader.IsDBNull(3) ? null : reader.GetString(3);
            var last = reader.IsDBNull(4) ? null : reader.GetString(4);
            var average = count == 0 ? 0 : total / count;
            return new PlaySessionStats(gameId, (int)count, total, average, longest, first, last);
        }, ct);

    /// <summary>某游戏最近若干条已结束会话（按开始时间倒序）。</summary>
    public Task<IReadOnlyList<PlaySessionRecord>> GetRecentSessionsAsync(
        string gameId, int topN = 10, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<PlaySessionRecord>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, GameId, StartedAt, EndedAt, EndReason, DurationSeconds
                FROM PlaySessions
                WHERE GameId = $g AND EndedAt IS NOT NULL AND EndReason <> 0
                ORDER BY StartedAt DESC
                LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$n", topN);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new PlaySessionRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    (PlaySessionEndReason)reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
            return (IReadOnlyList<PlaySessionRecord>)list;
        }, ct);

    /// <summary>全部已结束会话（CSV 导出用）。</summary>
    public Task<IReadOnlyList<PlaySessionRecord>> GetAllSessionsAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<PlaySessionRecord>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, GameId, StartedAt, EndedAt, EndReason, DurationSeconds
                FROM PlaySessions
                WHERE EndedAt IS NOT NULL
                ORDER BY GameId, StartedAt DESC
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new PlaySessionRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    (PlaySessionEndReason)reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
            return (IReadOnlyList<PlaySessionRecord>)list;
        }, ct);

    /// <summary>当前进行中（未结束）的会话数量（用于状态提示与测试）。</summary>
    public Task<int> CountRunningAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PlaySessions WHERE EndedAt IS NULL";
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }, ct);

    /// <summary>某游戏已结束（含短会话）的会话次数；用于判断“是否已玩过一次”。</summary>
    public Task<int> CountFinishedSessionsAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PlaySessions WHERE GameId = $g AND EndedAt IS NOT NULL";
            cmd.Parameters.AddWithValue("$g", gameId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }, ct);

    /// <summary>
    /// 本机产生（非仓库导入）且已结束的会话，用于发布到共享仓库。
    /// 导入的会话保持 IsImported=1，避免被再次发布成其他设备路径下的副本。
    /// </summary>
    public Task<IReadOnlyList<PlaySessionRecord>> GetFinishedLocalSessionsAsync(
        string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<PlaySessionRecord>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, GameId, StartedAt, EndedAt, EndReason, DurationSeconds
                FROM PlaySessions
                WHERE GameId = $g AND EndedAt IS NOT NULL AND IsImported = 0
                ORDER BY StartedAt
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new PlaySessionRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    (PlaySessionEndReason)reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
            return (IReadOnlyList<PlaySessionRecord>)list;
        }, ct);

    /// <summary>批量导入其他设备发布的会话（按 SessionId 去重，已存在则忽略）。</summary>
    public Task ImportSessionsAsync(
        IEnumerable<PlaySessionRecord> sessions, CancellationToken ct = default)
    {
        var items = sessions.ToList();
        if (items.Count == 0)
            return Task.CompletedTask;

        return _db.ExecuteAsync(async conn =>
        {
            foreach (var s in items)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO PlaySessions
                        (SessionId, GameId, StartedAt, EndedAt, LastSeenAt, EndReason, DurationSeconds, IsImported)
                    VALUES ($id, $g, $s, $e, $e, $r, $d, 1)
                    """;
                cmd.Parameters.AddWithValue("$id", s.SessionId);
                cmd.Parameters.AddWithValue("$g", s.GameId);
                cmd.Parameters.AddWithValue("$s", s.StartedAt);
                cmd.Parameters.AddWithValue("$e", s.EndedAt);
                cmd.Parameters.AddWithValue("$r", (int)s.EndReason);
                cmd.Parameters.AddWithValue("$d", s.DurationSeconds);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }
}

/// <summary>单条游戏会话记录。</summary>
public sealed record PlaySessionRecord(
    string SessionId,
    string GameId,
    string StartedAt,
    string EndedAt,
    PlaySessionEndReason EndReason,
    long DurationSeconds);

/// <summary>某游戏的会话汇总统计。</summary>
public sealed record PlaySessionStats(
    string GameId,
    int SessionCount,
    long TotalSeconds,
    long AverageSeconds,
    long LongestSeconds,
    string? FirstPlayedAt,
    string? LastPlayedAt)
{
    public static PlaySessionStats Empty(string gameId) => new(gameId, 0, 0, 0, 0, null, null);

    public bool HasData => SessionCount > 0;
}
