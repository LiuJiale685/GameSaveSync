using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace GameSaveSync.Desktop.Tray;

/// <summary>
/// 系统托盘（Hardcodet TaskbarIcon）：图标用代码绘制（无需外部资源），
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
            ToolTipText = "GameSaveSync — 游戏存档同步",
            ContextMenu = menu,
            Icon = CreateIcon(),
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

    /// <summary>用代码画一个简单的托盘图标（蓝色方块 + 白色“GS”）。</summary>
    private static Icon CreateIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(0x2B, 0x57, 0x9A));
            graphics.FillRectangle(background, 0, 0, size, size);
            using var font = new Font("Segoe UI", 13f, System.Drawing.FontStyle.Bold);
            using var foreground = new SolidBrush(Color.White);
            var textSize = graphics.MeasureString("GS", font);
            graphics.DrawString(
                "GS", font, foreground,
                (size - textSize.Width) / 2f, (size - textSize.Height) / 2f);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
