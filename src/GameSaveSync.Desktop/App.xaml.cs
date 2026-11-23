using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GameSaveSync.Core.Interfaces;
using GameSaveSync.Data;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Desktop.Services;
using GameSaveSync.Desktop.Tray;
using GameSaveSync.Desktop.ViewModels;
using GameSaveSync.Desktop.Views;
using GameSaveSync.Sync;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GameSaveSync.Desktop;

/// <summary>
/// 应用入口：DI 容器、Serilog 文件日志、单实例 Mutex（绑定仓库路径，T15）、
/// 启动初始化与未完成任务恢复（T11/T14）、全局异常处理。
/// </summary>
public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameSaveSync", "logs");

    private ServiceProvider? _services;
    private Mutex? _singleInstanceMutex;
    private TrayController? _tray;
    private GameProcessMonitorService? _monitor;

    public App()
    {
        ConfigureLogging();
        HookGlobalExceptions();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---------- 单实例检查（T15）：Mutex 名称绑定仓库路径 ----------
        SqliteDatabase? db = null;
        try
        {
            db = new SqliteDatabase(SqliteDatabase.DefaultDatabasePath());
            db.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var repoPath = new AppSettingsRepository(db)
                .GetAsync(AppSettingsRepository.RepoPathKey, CancellationToken.None)
                .GetAwaiter().GetResult();
            _singleInstanceMutex = new Mutex(initiallyOwned: true, BuildMutexName(repoPath), out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "GameSaveSync 已在运行（同一仓库仅允许一个实例写入）。本次启动将退出。",
                    "GameSaveSync", MessageBoxButton.OK, MessageBoxImage.Information);
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                db.Dispose();
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动初始化失败。");
            MessageBox.Show($"启动失败：{ex.Message}", "GameSaveSync", MessageBoxButton.OK, MessageBoxImage.Error);
            db?.Dispose();
            Shutdown();
            return;
        }

        try
        {
            _services = BuildServices(db);
            var appService = _services.GetRequiredService<GameSaveSyncAppService>();

            // ---------- 启动初始化（读取已保存的仓库配置并绑定） ----------
            appService.InitializeAsync(null, CancellationToken.None).GetAwaiter().GetResult();

            // ---------- 启动恢复：崩溃残留恢复回滚（T14）与未完成任务重置（T11） ----------
            appService.RecoverPendingRestoresAsync(CancellationToken.None).GetAwaiter().GetResult();
            var pendingTasks = appService.RecoverPendingTasksAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (pendingTasks.Count > 0)
                Log.Information("启动恢复：{Count} 个未完成任务已重置为待处理。", pendingTasks.Count);

            // ---------- 游戏进程监控（游戏退出自动备份，方案 6.3） ----------
            _monitor = _services.GetRequiredService<GameProcessMonitorService>();
            _monitor.BackupAsync = (gameId, ct) => appService.BackupNowAsync(gameId, "游戏退出自动备份", ct);
            _monitor.Start();

            // ---------- 主窗口与托盘 ----------
            var mainWindow = _services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            _tray = new TrayController(mainWindow);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动失败，程序将退出。");
            MessageBox.Show($"启动失败：{ex.Message}", "GameSaveSync", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static ServiceProvider BuildServices(SqliteDatabase db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<AppSettingsRepository>();
        services.AddSingleton<GameProcessMonitorService>();
        services.AddSingleton<IGameProcessChecker>(sp => sp.GetRequiredService<GameProcessMonitorService>());
        services.AddSingleton<GameSaveSyncAppService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<VersionsViewModel>();
        services.AddSingleton<SyncStatusViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    /// <summary>Mutex 名称：仓库路径 SHA256 前 16 位；未绑定仓库时用固定名称（T15）。</summary>
    private static string BuildMutexName(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            return "GameSaveSync-default";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repoPath)));
        return $"GameSaveSync-{hash[..16].ToLowerInvariant()}";
    }

    private static void ConfigureLogging()
    {
        Directory.CreateDirectory(LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(LogDirectory, "log.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("========== GameSaveSync 启动 ==========");
    }

    private void HookGlobalExceptions()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "未处理的 UI 异常。");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "未处理异常（AppDomain）。");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常。");
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _monitor?.Dispose();
            _tray?.Dispose();
            if (_services is not null)
            {
                _services.GetRequiredService<GameSaveSyncAppService>().DisposeAsync().AsTask()
                    .GetAwaiter().GetResult();
                _services.Dispose();
            }
            _singleInstanceMutex?.Dispose();
        }
        finally
        {
            Log.Information("========== GameSaveSync 退出 ==========");
            Log.CloseAndFlush();
        }
        base.OnExit(e);
    }
}
