// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Data.Repositories;
using GameSaveSync.Desktop.Services;
using GameSaveSync.Sync;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// 设置页：仓库目录绑定、设备信息与重新生成设备 ID（T20）、仓库统计、开机自启动开关。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly GameSaveSyncAppService _appService;
    private readonly AppSettingsRepository _settings;

    // ---- 仓库绑定 ----
    [ObservableProperty] private string _repoPathInput = string.Empty;
    [ObservableProperty] private string _boundRepoPath = "（未绑定）";

    // ---- 设备信息 ----
    [ObservableProperty] private string _deviceIdShort = "—";
    [ObservableProperty] private string _deviceName = "—";
    [ObservableProperty] private string _deviceCreatedAt = "—";

    // ---- 仓库统计 ----
    [ObservableProperty] private string _objectCountDisplay = "—";
    [ObservableProperty] private string _totalSizeDisplay = "—";

    // ---- 开机自启动 ----
    [ObservableProperty] private bool _autoStartEnabled;

    /// <summary>版本信息（“关于”卡片展示，By Jiale）。</summary>
    public string AppVersionDisplay { get; } =
        $"GameSaveSync v{typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    // ---- 外观 ----
    /// <summary>主题选项（跟随系统 / 浅色 / 深色）。</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(AppTheme.System, "跟随系统"),
        new ThemeOption(AppTheme.Light, "浅色"),
        new ThemeOption(AppTheme.Dark, "深色"),
    };

    [ObservableProperty] private ThemeOption? _selectedTheme;

    private bool _suppressTheme;
    private bool _themeLoaded;

    private bool _suppressAutoStart;
    private bool _autoStartLoaded;

    public SettingsViewModel(
        GameSaveSyncAppService appService,
        AppSettingsRepository settings,
        IDialogService dialog)
        : base(dialog)
    {
        _appService = appService;
        _settings = settings;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        RepoPathInput = _appService.RepoPath ?? string.Empty;
        BoundRepoPath = _appService.IsRepositoryBound
            ? _appService.RepoPath ?? "（未知）"
            : "（未绑定）";

        var device = _appService.Device;
        DeviceIdShort = FormatUtil.ShortId(device.DeviceId);
        DeviceName = device.DeviceName;
        DeviceCreatedAt = FormatUtil.FormatUtcTimestamp(device.CreatedAt);

        _suppressAutoStart = true;
        AutoStartEnabled = AutoStartService.IsEnabled();
        _autoStartLoaded = true;
        _suppressAutoStart = false;

        _suppressTheme = true;
        SelectedTheme = ThemeOptions.FirstOrDefault(o => o.Theme == ThemeService.Current) ?? ThemeOptions[0];
        _themeLoaded = true;
        _suppressTheme = false;

        await RefreshStatsAsync();
    }

    [RelayCommand]
    private void BrowseRepo()
    {
        var path = Dialog.PickFolder("选择同步仓库目录（如网盘同步盘下的 GameSaveVault）");
        if (path is not null)
            RepoPathInput = path;
    }

    [RelayCommand]
    private async Task BindRepoAsync()
    {
        var path = RepoPathInput?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            Dialog.Error("请先填写或选择仓库目录。");
            return;
        }
        if (!System.IO.Directory.Exists(path))
        {
            Dialog.Error($"目录不存在：{path}");
            return;
        }

        // 已绑定且指向不同目录：直接改绑会丢掉历史版本，必须走"迁移仓库"。
        if (_appService.IsRepositoryBound
            && !string.Equals(
                System.IO.Path.GetFullPath(path),
                System.IO.Path.GetFullPath(_appService.RepoPath ?? string.Empty),
                System.StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Error(
                "当前已绑定仓库。更换同步目录请使用“迁移仓库到新目录…”：" +
                "直接改绑会连接到空目录，导致历史版本丢失。");
            return;
        }

        var ok = await RunBusyAsync(async () =>
        {
            // 绑定仓库并把路径持久化到本地设置（下次启动 InitializeAsync 会自动绑定）
            await _appService.BindRepositoryAsync(path);
            await _settings.SetAsync(AppSettingsRepository.RepoPathKey, path);
        }, "正在绑定仓库…");
        if (!ok)
            return;

        BoundRepoPath = path;
        RepoPathInput = path;
        Dialog.Info($"仓库已绑定：{path}");
        await RefreshStatsAsync();
    }

    /// <summary>
    /// 迁移仓库到新目录（更换网盘同步目录时使用）：复制全部历史并校验，
    /// 成功后自动切换绑定；可在确认对话框选择是否删除旧目录中的数据。
    /// </summary>
    [RelayCommand]
    private async Task MigrateRepoAsync()
    {
        if (!_appService.IsRepositoryBound)
        {
            Dialog.Error("当前尚未绑定仓库，请先绑定或初始化一个仓库。");
            return;
        }

        var path = RepoPathInput?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            Dialog.Error("请先填写或选择新的仓库目录。");
            return;
        }
        if (string.Equals(
                System.IO.Path.GetFullPath(path),
                System.IO.Path.GetFullPath(_appService.RepoPath ?? string.Empty),
                System.StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Error("新目录与当前仓库相同，无需迁移。");
            return;
        }
        if (System.IO.Directory.Exists(path)
            && System.IO.Directory.EnumerateFileSystemEntries(path).Any()
            && !System.IO.File.Exists(System.IO.Path.Combine(path, "repo.json")))
        {
            Dialog.Error($"目标目录非空且不是 GameSaveSync 仓库：{path}");
            return;
        }

        if (!Dialog.Confirm(
                $"将把当前仓库的全部历史（对象、清单、提交记录）复制到：\n{path}\n\n" +
                "复制过程会逐个校验文件哈希，完成后自动切换绑定。" +
                "全部分区完成后才会切换，中途失败不会影响当前仓库。",
                "迁移仓库到新目录"))
            return;

        var clearSource = Dialog.Confirm(
            "迁移成功后是否删除旧目录中的仓库数据？\n\n" +
            "选择“是”：删除旧目录（释放空间，但旧目录若仍在网盘同步范围内会被同步删除）。\n" +
            "选择“否”：保留旧目录作为备份，需你稍后手动清理。",
            "是否清理旧目录");

        RepoMigrationResult? result = null;
        var ok = await RunBusyAsync(async () =>
        {
            result = await _appService.MigrateRepositoryAsync(path, clearSource);
            await _settings.SetAsync(AppSettingsRepository.RepoPathKey, path);
        }, "正在迁移仓库（复制并校验全部历史）…");
        if (!ok || result is null)
            return;

        BoundRepoPath = result.NewRepoPath;
        RepoPathInput = result.NewRepoPath;

        var summary =
            $"仓库迁移完成：\n{result.NewRepoPath}\n\n" +
            $"复制 {result.CopiedFileCount} 个文件，共 {FormatUtil.FormatBytes(result.CopiedBytes)}。" +
            "\n仓库 ID 保持不变，历史版本与其他设备的记录继续有效。";
        if (result.SourceCleared)
            summary += "\n旧目录已删除。";
        else if (result.CleanupError is not null)
            summary += $"\n旧目录清理失败：{result.CleanupError}\n可稍后手动删除。";
        else
            summary += "\n旧目录已保留，可稍后手动清理。";
        Dialog.Info(summary);

        await RefreshStatsAsync();
    }

    [RelayCommand]
    private async Task RegenerateDeviceIdAsync()
    {
        if (!Dialog.Confirm(
                "确定重新生成设备 ID 吗？\n\n" +
                "这通常只在系统环境被整体克隆/复制后使用（避免两台设备共用同一设备身份）。" +
                "重新生成后本机将以新设备身份写入仓库。",
                "重新生成设备 ID"))
            return;

        DeviceInfoRecord? device = null;
        var ok = await RunBusyAsync(
            async () => device = await _appService.RegenerateDeviceIdAsync(),
            "正在重新生成设备 ID…");
        if (!ok || device is null)
            return;

        DeviceIdShort = FormatUtil.ShortId(device.DeviceId);
        DeviceName = device.DeviceName;
        DeviceCreatedAt = FormatUtil.FormatUtcTimestamp(device.CreatedAt);
        Dialog.Info($"已重新生成设备 ID：{DeviceIdShort}…");
    }

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        if (!_appService.IsRepositoryBound)
        {
            ObjectCountDisplay = "—（未绑定仓库）";
            TotalSizeDisplay = "—（未绑定仓库）";
            return;
        }

        (int Count, long TotalBytes) stats = default;
        var ok = await RunBusyAsync(
            async () => stats = await _appService.GetRepoStatsAsync(),
            "正在统计仓库对象…");
        if (!ok)
            return;

        ObjectCountDisplay = $"{stats.Count} 个对象";
        TotalSizeDisplay = FormatUtil.FormatBytes(stats.TotalBytes);
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (_suppressTheme || !_themeLoaded || value is null)
            return;
        ThemeService.Apply(value.Theme);
        _ = _settings.SetAsync(AppSettingsRepository.ThemeKey, value.Theme.ToString());
        StatusText = $"外观已切换为：{value.DisplayName}。";
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        // 仅在初次加载完成后响应用户切换（默认关闭）
        if (_suppressAutoStart || !_autoStartLoaded)
            return;
        try
        {
            AutoStartService.SetEnabled(value);
            StatusText = value ? "已开启开机自启动。" : "已关闭开机自启动。";
        }
        catch (System.Exception ex)
        {
            Dialog.Error($"设置开机自启动失败：{ex.Message}");
            _suppressAutoStart = true;
            AutoStartEnabled = !value;
            _suppressAutoStart = false;
        }
    }
}

/// <summary>外观主题下拉项。</summary>
public sealed record ThemeOption(AppTheme Theme, string DisplayName);
