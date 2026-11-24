using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using GameSaveSync.Desktop.Services;

namespace GameSaveSync.Desktop.Views.Dialogs;

/// <summary>“添加游戏 / 从仓库绑定已有游戏 / 编辑游戏”对话框。</summary>
public partial class AddGameWindow : Window
{
    private readonly bool _bindFromRepo;

    /// <summary>用户确认后的输入结果（存档目录已校验存在）。</summary>
    public AddGameResult? Result { get; private set; }

    public AddGameWindow(bool bindFromRepo, IReadOnlyList<SharedGameInfo> repoGames)
    {
        InitializeComponent();
        _bindFromRepo = bindFromRepo;

        Title = bindFromRepo ? "从仓库绑定已有游戏" : "添加游戏";
        ModeText.Text = bindFromRepo
            ? "从仓库的共享游戏中选择一个绑定到本机（复用共享 gameId，T17），并映射本机存档目录。"
            : "新建游戏：生成新的共享 gameId 并写入仓库 game.json。";

        SharedCombo.ItemsSource = repoGames;
        SharedCombo.DisplayMemberPath = nameof(SharedGameInfo.Name);
        SharedPanel.Visibility = bindFromRepo ? Visibility.Visible : Visibility.Collapsed;
        if (repoGames.Count > 0)
        {
            SharedCombo.SelectedIndex = 0;
        }
    }

    /// <summary>编辑模式：以已有配置预填各字段，gameId 保持不变。</summary>
    public AddGameWindow(LocalGameConfig game)
    {
        InitializeComponent();
        _bindFromRepo = false;

        Title = "编辑游戏";
        ModeText.Text = "修改本机游戏配置。gameId 保持不变，存档目录与游戏程序只影响本机。";
        SharedPanel.Visibility = Visibility.Collapsed;

        NameBox.Text = game.Name;
        PathBox.Text = game.SavePath;
        ProcessBox.Text = string.Join(", ", game.ProcessNames);
        ExeBox.Text = game.ExecutablePath;
    }

    private void SharedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SharedCombo.SelectedItem is SharedGameInfo shared)
            NameBox.Text = shared.Name;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏存档目录" };
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FolderName;
    }

    private void DetectProcess_Click(object sender, RoutedEventArgs e)
    {
        var exePath = ExeBox.Text.Trim();
        if (exePath.Length == 0)
        {
            MessageBox.Show(this, "请先填写或选择“游戏程序”，再自动识别进程名。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ExeBox.Focus();
            return;
        }

        var names = GameProcessMonitorService.DetectRunningProcessNames(exePath);
        if (names.Count == 0)
        {
            MessageBox.Show(this,
                "未检测到正在运行的进程。请先启动游戏，等进入游戏后再点“自动识别”。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ProcessBox.Text = string.Join(", ", names);
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            ExeBox.Text = dialog.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var savePath = PathBox.Text.Trim();
        var exePath = ExeBox.Text.Trim();

        if (name.Length == 0)
        {
            MessageBox.Show(this, "请填写游戏名称。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }
        if (savePath.Length == 0 || !Directory.Exists(savePath))
        {
            MessageBox.Show(this, "存档目录不存在，请通过“浏览…”选择或输入有效目录。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            PathBox.Focus();
            return;
        }
        if (exePath.Length > 0 && !File.Exists(exePath))
        {
            MessageBox.Show(this, "游戏程序文件不存在，请通过“浏览…”重新选择，或留空跳过。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ExeBox.Focus();
            return;
        }

        string sharedGameId = string.Empty;
        if (_bindFromRepo)
        {
            if (SharedCombo.SelectedItem is SharedGameInfo shared)
                sharedGameId = shared.GameId;
            else
            {
                MessageBox.Show(this, "请选择要绑定的仓库共享游戏。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var processNames = ProcessNameNormalizer.NormalizeAll(ProcessBox.Text
            .Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // 未填进程名但选了 exe 时，用 exe 文件名作为默认进程名，便于状态检测与停止
        if (processNames.Count == 0 && exePath.Length > 0)
        {
            var exeName = Path.GetFileName(exePath);
            if (!string.IsNullOrWhiteSpace(exeName))
                processNames.Add(ProcessNameNormalizer.Normalize(exeName));
        }

        Result = new AddGameResult(sharedGameId, name, savePath, processNames, exePath);
        DialogResult = true;
    }
}
