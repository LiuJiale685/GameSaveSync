// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
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
    /// <param name="isRunning">游戏正在运行：仅开放“自动识别进程名”，以及存档目录为空时补填存档目录。</param>
    public AddGameWindow(LocalGameConfig game, bool isRunning = false)
    {
        InitializeComponent();
        _bindFromRepo = false;

        Title = "编辑游戏";
        SharedPanel.Visibility = Visibility.Collapsed;

        NameBox.Text = game.Name;
        PathBox.Text = game.SavePath;
        ProcessBox.Text = string.Join(", ", game.ProcessNames);
        ExeBox.Text = game.ExecutablePath;

        if (isRunning)
            ApplyRunningRestrictions(hasSavePath: !string.IsNullOrWhiteSpace(game.SavePath));
        else
            ModeText.Text = "修改本机游戏配置。gameId 保持不变，存档目录与游戏程序只影响本机。";
    }

    /// <summary>
    /// 运行中编辑的限制：只放开“自动识别”按钮；存档目录为空时额外放开补填，
    /// 其余字段（名称、进程名、游戏程序）一律只读，避免运行期改错导致监控失联。
    /// </summary>
    private void ApplyRunningRestrictions(bool hasSavePath)
    {
        ModeText.Text = hasSavePath
            ? "游戏正在运行中：仅支持用“自动识别”补全进程名。如需修改其他配置，请先停止游戏。"
            : "游戏正在运行中：可用“自动识别”补全进程名，也可补填存档目录（备份与同步需要）。其他配置请先停止游戏再修改。";

        NameBox.IsReadOnly = true;
        ExeBox.IsReadOnly = true;
        // 进程名只允许通过“自动识别”写入，避免运行期手填出错导致监控失联
        ProcessBox.IsReadOnly = true;
        BrowseExeButton.Visibility = Visibility.Collapsed;

        if (hasSavePath)
        {
            PathBox.IsReadOnly = true;
            BrowsePathButton.Visibility = Visibility.Collapsed;
        }
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
        // 存档目录可留空（首次添加游戏往往还不确定位置），稍后可在“编辑”中补填。
        if (savePath.Length > 0 && !Directory.Exists(savePath))
        {
            MessageBox.Show(this, "存档目录不存在，请通过“浏览…”选择或输入有效目录（也可留空稍后填写）。",
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
