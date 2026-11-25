// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.ComponentModel;
using System.Windows;
using GameSaveSync.Desktop.ViewModels;

namespace GameSaveSync.Desktop.Views;

/// <summary>
/// 主窗口（四个页签）。关闭按钮 = 最小化到托盘；真正退出走托盘菜单“退出”。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>是否允许真正关闭（托盘“退出”时置 true）。</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoadedOnce;
    }

    private async void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOnce;
        try
        {
            await _viewModel.LoadInitialAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"初始数据加载失败：{ex.Message}", "GameSaveSync",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!AllowClose)
        {
            // 关闭按钮 → 最小化到托盘
            e.Cancel = true;
            Hide();
        }
    }
}
