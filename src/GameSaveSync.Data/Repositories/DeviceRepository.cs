// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Data.Repositories;

/// <summary>设备信息与游戏配置、路径映射（方案 4.2/4.3）。</summary>
public sealed class DeviceRepository
{
    private readonly SqliteDatabase _db;

    public DeviceRepository(SqliteDatabase db) => _db = db;

    public Task<DeviceInfoRecord?> GetDeviceAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DeviceId, DeviceName, CreatedAt FROM Devices LIMIT 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new DeviceInfoRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2));
            return null;
        }, ct);

    /// <summary>获取或创建本机设备身份（方案 7.6）。</summary>
    public Task<DeviceInfoRecord> GetOrCreateDeviceAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DeviceId, DeviceName, CreatedAt FROM Devices LIMIT 1";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    return new DeviceInfoRecord(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }

            var device = new DeviceInfoRecord(
                RepoPaths.NormalizeGuid(Guid.NewGuid()),
                Environment.MachineName,
                UtcClock.Now());
            await using (var insert = conn.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO Devices (DeviceId, DeviceName, CreatedAt) VALUES ($id, $name, $created)";
                insert.Parameters.AddWithValue("$id", device.DeviceId);
                insert.Parameters.AddWithValue("$name", device.DeviceName);
                insert.Parameters.AddWithValue("$created", device.CreatedAt);
                await insert.ExecuteNonQueryAsync(ct);
            }
            return device;
        }, ct);

    /// <summary>重新生成设备 ID（环境被克隆时使用，方案 7.6 / T20）。</summary>
    public Task<DeviceInfoRecord> RegenerateDeviceAsync(CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM Devices";
            await del.ExecuteNonQueryAsync(ct);

            await using var del2 = conn.CreateCommand();
            del2.CommandText = "DELETE FROM GamePaths";
            await del2.ExecuteNonQueryAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);

            var device = new DeviceInfoRecord(
                RepoPaths.NormalizeGuid(Guid.NewGuid()),
                Environment.MachineName,
                UtcClock.Now());
            await using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO Devices (DeviceId, DeviceName, CreatedAt) VALUES ($id, $name, $created)";
            insert.Parameters.AddWithValue("$id", device.DeviceId);
            insert.Parameters.AddWithValue("$name", device.DeviceName);
            insert.Parameters.AddWithValue("$created", device.CreatedAt);
            await insert.ExecuteNonQueryAsync(ct);
            return device;
        }, ct);
}

public sealed record DeviceInfoRecord(string DeviceId, string DeviceName, string CreatedAt);
