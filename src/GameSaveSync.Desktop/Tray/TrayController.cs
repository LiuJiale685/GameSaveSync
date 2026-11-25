// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace GameSaveSync.Desktop.Tray;

/// <summary>
/// 系统托盘（Hardcodet TaskbarIcon）：图标取自应用图标资源 Assets/GameSaveSync.ico，
/// 右键菜单：显示主窗口 / 退出；点 X 只是隐藏到托盘，真正退出走托盘菜单。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Window _mainWindow;

    public TrayController(Window mainWindow)
    {
        _mainWindow = mainWindow;

        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "显示主窗口(_S)" };
        showItem.Click += (_, _) => ShowMainWindow();
        var exitItem = new MenuItem { Header = "退出(_X)" };
        exitItem.Click += (_, _) => RequestExit();
        menu.Items.Add(showItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "GameSaveSync — 游戏存档同步（By Jiale）",
            ContextMenu = menu,
            Icon = LoadIcon(),
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void RequestExit()
    {
        if (_mainWindow is Views.MainWindow window)
            window.AllowClose = true;
        Log.Information("用户通过托盘菜单请求退出。");
        Application.Current.Shutdown();
    }

    /// <summary>从应用资源加载托盘图标（Assets/GameSaveSync.ico，ICO 内含多种尺寸）。</summary>
    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/GameSaveSync.ico"));
        if (resource is null)
            throw new InvalidOperationException("未找到图标资源 Assets/GameSaveSync.ico。");
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream);
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
