using Microsoft.Data.Sqlite;

namespace GameSaveSync.Data.Repositories;

/// <summary>
/// 应用本地设置（Meta 表）：仓库目录路径等。
/// </summary>
public sealed class AppSettingsRepository
{
    private readonly SqliteDatabase _db;

    public AppSettingsRepository(SqliteDatabase db) => _db = db;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Meta WHERE Key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }, ct);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => _db.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Meta (Key, Value) VALUES ($k, $v)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public const string RepoPathKey = "repoPath";

    /// <summary>外观主题设置（0=跟随系统，1=浅色，2=深色）。</summary>
    public const string ThemeKey = "themeMode";
}
