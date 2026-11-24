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
                INSERT INTO Games (GameId, Name, ProcessNames, ExecutablePath, AutoBackup, AutoRestore, CreatedAt)
                VALUES ($id, $name, $procs, $exe, $autoB, $autoR, $created)
                ON CONFLICT(GameId) DO UPDATE SET
                    Name = excluded.Name,
                    ProcessNames = excluded.ProcessNames,
                    ExecutablePath = excluded.ExecutablePath,
                    AutoBackup = excluded.AutoBackup,
                    AutoRestore = excluded.AutoRestore
                """;
            cmd.Parameters.AddWithValue("$id", config.GameId);
            cmd.Parameters.AddWithValue("$name", config.Name);
            cmd.Parameters.AddWithValue("$procs", string.Join(';', config.ProcessNames));
            cmd.Parameters.AddWithValue("$exe", config.ExecutablePath ?? string.Empty);
            cmd.Parameters.AddWithValue("$autoB", config.AutoBackup ? 1 : 0);
            cmd.Parameters.AddWithValue("$autoR", config.AutoRestore ? 1 : 0);
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
            cmd.CommandText = "SELECT GameId, Name, ProcessNames, AutoBackup, AutoRestore, ExecutablePath FROM Games ORDER BY Name";
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
                });
            }
            return (IReadOnlyList<LocalGameConfig>)list;
        }, ct);

    public Task<LocalGameConfig?> GetGameAsync(string gameId, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GameId, Name, ProcessNames, AutoBackup, AutoRestore, ExecutablePath FROM Games WHERE GameId = $id";
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
