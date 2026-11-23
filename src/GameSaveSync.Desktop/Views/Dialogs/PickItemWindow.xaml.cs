using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace GameSaveSync.Desktop.Views.Dialogs;

/// <summary>简单列表选择对话框（如：从冲突的多个远端 Commit 中选择一个）。</summary>
public partial class PickItemWindow : Window
{
    /// <summary>用户选中的项（未选择/取消时为 null）。</summary>
    public string? SelectedItem { get; private set; }

    public PickItemWindow(string prompt, IReadOnlyList<string> items)
    {
        InitializeComponent();
        Title = prompt.Length > 40 ? "请选择" : prompt;
        PromptText.Text = prompt;
        ItemList.ItemsSource = items;
        if (items.Count > 0)
            ItemList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not string selected)
        {
            MessageBox.Show(this, "请先选中一项。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedItem = selected;
        DialogResult = true;
    }

    private void ItemList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is string)
            Ok_Click(sender, e);
    }
}
