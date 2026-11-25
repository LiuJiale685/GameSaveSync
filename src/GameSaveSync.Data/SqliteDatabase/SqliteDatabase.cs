// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using Microsoft.Data.Sqlite;
using Serilog;

namespace GameSaveSync.Data;

/// <summary>
/// 本地 SQLite 数据库（方案 4.1/4.2）。
/// 数据库只保存在设备本地（默认 %LOCALAPPDATA%/GameSaveSync/state.db），
/// 绝不放入云盘同步目录。
/// </summary>
public sealed class SqliteDatabase : IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDatabase(string dbFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbFilePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        DbFilePath = dbFilePath;
    }

    public string DbFilePath { get; }

    public static string DefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "GameSaveSync", "state.db");
    }

    /// <summary>创建连接并确保库结构存在。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async conn =>
        {
            await Schema.CreateAsync(conn, cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
        Log.Information("本地数据库已初始化: {DbPath}", DbFilePath);
    }

    /// <summary>串行化执行（SQLite 单写者，简单起见全局串行）。</summary>
    public async Task ExecuteAsync(Func<SqliteConnection, Task> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await action(connection).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await action(connection).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }

    public void Dispose() => _gate.Dispose();
}
