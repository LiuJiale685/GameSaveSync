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
