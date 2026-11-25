// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Collections.Generic;
using System.Windows;
using GameSaveSync.Core.Models;
using GameSaveSync.Desktop.Views.Dialogs;
using Microsoft.Win32;

namespace GameSaveSync.Desktop.Services;

/// <summary>“添加游戏 / 从仓库绑定已有游戏”对话框的返回结果。</summary>
/// <param name="SharedGameId">绑定模式：选中的仓库共享游戏 gameId；新建模式为空字符串。</param>
/// <param name="Name">本机显示名称。</param>
/// <param name="SavePath">本机存档目录（已校验存在）。</param>
/// <param name="ProcessNames">进程名列表（统一为带 .exe 后缀的格式，已去重）。</param>
/// <param name="ExecutablePath">可选游戏可执行文件路径（空字符串表示未配置）。</param>
public sealed record AddGameResult(
    string SharedGameId,
    string Name,
    string SavePath,
    IReadOnlyList<string> ProcessNames,
    string ExecutablePath);

/// <summary>UI 对话框入口（确认 / 提示 / 错误 / 选择目录 / 添加游戏 / 选择列表项 / 查看清单）。</summary>
public interface IDialogService
{
    bool Confirm(string message, string title = "GameSaveSync");
    void Info(string message, string title = "GameSaveSync");
    void Error(string message, string title = "GameSaveSync");
    string? PickFolder(string title);

    /// <summary>另存文件对话框；返回选中路径，取消返回 null。</summary>
    string? PickSaveFile(string title, string filter, string defaultFileName);
    AddGameResult? ShowAddGameDialog(bool bindFromRepo, IReadOnlyList<SharedGameInfo> repoGames);

    /// <summary>编辑已有游戏配置（名称、存档目录、进程名、游戏程序）。</summary>
    AddGameResult? ShowEditGameDialog(LocalGameConfig game);
    string? PickItem(string prompt, IReadOnlyList<string> items);
    void ShowManifest(string title, IReadOnlyList<ManifestFileEntry> entries);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public bool Confirm(string message, string title = "GameSaveSync")
        => MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    public void Info(string message, string title = "GameSaveSync")
        => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string title = "GameSaveSync")
        => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public AddGameResult? ShowAddGameDialog(bool bindFromRepo, IReadOnlyList<SharedGameInfo> repoGames)    {
        var window = new AddGameWindow(bindFromRepo, repoGames) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public AddGameResult? ShowEditGameDialog(LocalGameConfig game)
    {
        var window = new AddGameWindow(game) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public string? PickItem(string prompt, IReadOnlyList<string> items)
    {
        var window = new PickItemWindow(prompt, items) { Owner = Owner };
        return window.ShowDialog() == true ? window.SelectedItem : null;
    }

    public void ShowManifest(string title, IReadOnlyList<ManifestFileEntry> entries)
    {
        var window = new ManifestWindow(title, entries) { Owner = Owner };
        window.ShowDialog();
    }
}
