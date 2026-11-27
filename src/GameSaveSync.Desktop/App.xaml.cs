// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
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
    private PlaySessionRecorder? _sessionRecorder;

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

            // ---------- 外观主题（在创建任何窗口前应用，避免闪烁） ----------
            var savedTheme = _services.GetRequiredService<AppSettingsRepository>()
                .GetAsync(AppSettingsRepository.ThemeKey, CancellationToken.None)
                .GetAwaiter().GetResult();
            ThemeService.Apply(Enum.TryParse<AppTheme>(savedTheme, out var theme) ? theme : AppTheme.System);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

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

            // ---------- 游戏时长会话记录（开始 / 心跳 / 结束） ----------
            _sessionRecorder = _services.GetRequiredService<PlaySessionRecorder>();
            _sessionRecorder.StartAsync().GetAwaiter().GetResult();
            _sessionRecorder.SessionFinishedAsync =
                (gameId, ct) => appService.SyncPlaySessionsAsync(gameId, ct, force: true);
            _monitor.SessionTickAsync = (gameId, running, ct) => _sessionRecorder.OnTickAsync(gameId, running, ct);

            _monitor.Start();

            // ---------- 主窗口与托盘 ----------
            var mainWindow = _services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            _tray = new TrayController(mainWindow);

            // ---------- 后台更新检查（“提醒式”：只提示，不下载） ----------
            _ = CheckForUpdatesAsync(mainWindow, _services.GetRequiredService<UpdateService>());
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
        services.AddSingleton<PlaySessionRepository>();
        services.AddSingleton<PlaySessionRecorder>();
        services.AddSingleton<GameProcessMonitorService>();
        services.AddSingleton<IGameProcessChecker>(sp => sp.GetRequiredService<GameProcessMonitorService>());
        services.AddSingleton<GameSaveSyncAppService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<VersionsViewModel>();
        services.AddSingleton<SyncStatusViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 启动后台检查更新：有新版时弹托盘气泡，点击打开 GitHub 发布页。
    /// 全程静默，不阻塞启动、不弹错误框。
    /// </summary>
    private async Task CheckForUpdatesAsync(Window mainWindow, UpdateService updateService)
    {
        try
        {
            var result = await updateService.CheckAsync(force: false);
            if (!result.HasUpdate || result.LatestVersion is null)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                _tray?.ShowBalloon(
                    "发现新版本",
                    $"GameSaveSync v{result.LatestVersion.ToString(3)} 已发布，点击查看。",
                    onClick: () =>
                    {
                        UpdateService.OpenReleasesPage();
                        if (mainWindow is Views.MainWindow window)
                        {
                            window.Show();
                            window.WindowState = WindowState.Normal;
                            window.Activate();
                        }
                    });
                Log.Information("提示更新：v{Version}。", result.LatestVersion.ToString(3));
            });
        }
        catch (Exception ex)
        {
            Log.Information(ex, "启动更新检查失败（已忽略）。");
        }
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
        Log.Information("========== GameSaveSync 启动（By Jiale） ==========");
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

    /// <summary>主题设置为“跟随系统”时，响应 Windows 深色模式切换。</summary>
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General
            && ThemeService.Current == AppTheme.System)
        {
            Dispatcher.Invoke(() => ThemeService.Apply(AppTheme.System));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try
        {
            _monitor?.Dispose();
            // 游戏仍在运行：会话记到退出时刻（AppClosed）
            try
            {
                _sessionRecorder?.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "退出时收尾游戏会话失败。");
            }
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
