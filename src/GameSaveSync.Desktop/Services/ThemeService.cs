using System;
using System.Windows;
using Microsoft.Win32;

namespace GameSaveSync.Desktop.Services;

/// <summary>外观主题选项。</summary>
public enum AppTheme
{
    /// <summary>跟随系统（Windows 应用深色模式设置）。</summary>
    System = 0,

    Light = 1,

    Dark = 2,
}

/// <summary>
/// 主题服务：切换 WPF 内置 Fluent 主题（<see cref="Application.ThemeMode"/>）并替换自定义配色字典。
/// 需在 UI 线程调用。
/// </summary>
public static class ThemeService
{
    private const string LightPaletteUri = "Themes/LightTheme.xaml";
    private const string DarkPaletteUri = "Themes/DarkTheme.xaml";

    private static ResourceDictionary? _palette;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>解析实际生效的外观（System 时按系统设置判定）。</summary>
    public static bool IsDarkEffective(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDark(),
    };

    /// <summary>应用主题：设置控件主题并替换当前配色字典。</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;

        if (Application.Current is { } app)
        {
            app.ThemeMode = theme switch
            {
                AppTheme.Dark => ThemeMode.Dark,
                AppTheme.Light => ThemeMode.Light,
                _ => ThemeMode.System,
            };
            SetPalette(IsDarkEffective(theme));
        }
    }

    /// <summary>读取系统“应用使用深色模式”设置（读取失败按浅色处理）。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SetPalette(bool dark)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkPaletteUri : LightPaletteUri, UriKind.Relative),
        };

        if (_palette is not null && merged.Remove(_palette))
        {
            merged.Add(palette);
        }
        else
        {
            // 首次调用（或字典已被清理）：移除 App.xaml 中预置的配色字典后追加。
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source?.OriginalString;
                if (source is not null
                    && (source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                        || source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    merged.RemoveAt(i);
                }
            }
            merged.Add(palette);
        }

        _palette = palette;
    }
}
