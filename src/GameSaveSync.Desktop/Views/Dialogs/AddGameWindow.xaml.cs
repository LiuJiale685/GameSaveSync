using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GameSaveSync.Core.Models;
using GameSaveSync.Desktop.Services;

namespace GameSaveSync.Desktop.Views.Dialogs;

/// <summary>“添加游戏 / 从仓库绑定已有游戏”对话框。</summary>
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var savePath = PathBox.Text.Trim();

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

        var processNames = ProcessBox.Text
            .Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Result = new AddGameResult(sharedGameId, name, savePath, processNames);
        DialogResult = true;
    }
}
