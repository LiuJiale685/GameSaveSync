// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Data.Repositories;

/// <summary>
/// 游戏配置与路径映射（方案 4.2）。本机 SavePath 只存本地 SQLite，不写入共享仓库。
/// </summary>
public sealed class GameRepository
{
    private readonly SqliteDatabase _db;

    public GameRepository(SqliteDatabase db) => _db = db;

    public Task AddOrUpdateGameAsync(LocalGameConfig config, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Games (GameId, Name, ProcessNames, ExecutablePath, AutoBackup, AutoRestore, Hidden, CreatedAt)
                VALUES ($id, $name, $procs, $exe, $autoB, $autoR, $hidden, $created)
                ON CONFLICT(GameId) DO UPDATE SET
                    Name = excluded.Name,
                    ProcessNames = excluded.ProcessNames,
                    ExecutablePath = excluded.ExecutablePath,
                    AutoBackup = excluded.AutoBackup,
                    AutoRestore = excluded.AutoRestore,
                    Hidden = excluded.Hidden
                """;
            cmd.Parameters.AddWithValue("$id", config.GameId);
            cmd.Parameters.AddWithValue("$name", config.Name);
            cmd.Parameters.AddWithValue("$procs", string.Join(';', config.ProcessNames));
            cmd.Parameters.AddWithValue("$exe", config.ExecutablePath ?? string.Empty);
            cmd.Parameters.AddWithValue("$autoB", config.AutoBackup ? 1 : 0);
            cmd.Parameters.AddWithValue("$autoR", config.AutoRestore ? 1 : 0);
            cmd.Parameters.AddWithValue("$hidden", config.IsHidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$created", UtcClock.Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task RemoveGameAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Games WHERE GameId = $id; DELETE FROM GamePaths WHERE GameId = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyList<LocalGameConfig>> ListGamesAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<LocalGameConfig>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GameId, Name, ProcessNames, AutoBackup, AutoRestore, ExecutablePath, Hidden FROM Games ORDER BY Name";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new LocalGameConfig
                {
                    GameId = reader.GetString(0),
                    Name = reader.GetString(1),
                    ProcessNames = reader.GetString(2)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    AutoBackup = reader.GetInt64(3) != 0,
                    AutoRestore = reader.GetInt64(4) != 0,
                    ExecutablePath = reader.GetString(5),
                    IsHidden = reader.GetInt64(6) != 0,
                });
            }
            return (IReadOnlyList<LocalGameConfig>)list;
        }, ct);

    /// <summary>列出游戏并带出指定设备的本机存档路径（未映射时 SavePath 为空串）。</summary>
    public Task<IReadOnlyList<LocalGameConfig>> ListGamesWithSavePathAsync(
        string deviceId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            var list = new List<LocalGameConfig>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT g.GameId, g.Name, g.ProcessNames, g.AutoBackup, g.AutoRestore, g.ExecutablePath,
                       COALESCE(p.SavePath, ''), g.Hidden
                FROM Games g
                LEFT JOIN GamePaths p ON p.GameId = g.GameId AND p.DeviceId = $d
                ORDER BY g.Name
                """;
            cmd.Parameters.AddWithValue("$d", deviceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new LocalGameConfig
                {
                    GameId = reader.GetString(0),
                    Name = reader.GetString(1),
                    ProcessNames = reader.GetString(2)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    AutoBackup = reader.GetInt64(3) != 0,
                    AutoRestore = reader.GetInt64(4) != 0,
                    ExecutablePath = reader.GetString(5),
                    SavePath = reader.GetString(6),
                    IsHidden = reader.GetInt64(7) != 0,
                });
            }
            return (IReadOnlyList<LocalGameConfig>)list;
        }, ct);

    public Task<LocalGameConfig?> GetGameAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GameId, Name, ProcessNames, AutoBackup, AutoRestore, ExecutablePath, Hidden FROM Games WHERE GameId = $id";
            cmd.Parameters.AddWithValue("$id", gameId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return new LocalGameConfig
            {
                GameId = reader.GetString(0),
                Name = reader.GetString(1),
                ProcessNames = reader.GetString(2)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                AutoBackup = reader.GetInt64(3) != 0,
                AutoRestore = reader.GetInt64(4) != 0,
                ExecutablePath = reader.GetString(5),
                IsHidden = reader.GetInt64(6) != 0,
            };
        }, ct);

    /// <summary>设置本机设备的存档路径映射（方案 4.3）。</summary>
    public Task SetSavePathAsync(string gameId, string deviceId, string savePath, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO GamePaths (GameId, DeviceId, SavePath)
                VALUES ($g, $d, $p)
                ON CONFLICT(GameId, DeviceId) DO UPDATE SET SavePath = excluded.SavePath
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$p", savePath);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<string?> GetSavePathAsync(string gameId, string deviceId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SavePath FROM GamePaths WHERE GameId = $g AND DeviceId = $d";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$d", deviceId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }, ct);
}
