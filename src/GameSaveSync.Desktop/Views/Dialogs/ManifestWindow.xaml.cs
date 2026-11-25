// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GameSaveSync.Core.Models;
using GameSaveSync.Desktop.ViewModels;

namespace GameSaveSync.Desktop.Views.Dialogs;

/// <summary>版本文件清单展示（path / size）。</summary>
public partial class ManifestWindow : Window
{
    private sealed record FileRow(string FilePath, string SizeText);

    public ManifestWindow(string title, IReadOnlyList<ManifestFileEntry> entries)
    {
        InitializeComponent();
        Title = "文件清单";
        HeaderText.Text = title;
        FileList.ItemsSource = entries
            .Select(entry => new FileRow(entry.Path, FormatUtil.FormatBytes(entry.Size)))
            .ToList();
    }
}
