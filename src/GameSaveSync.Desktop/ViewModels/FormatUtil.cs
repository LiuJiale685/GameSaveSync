// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using GameSaveSync.Core.Services;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>显示格式化工具（id 截断、时间本地化、字节体积）。</summary>
public static class FormatUtil
{
    /// <summary>id 前 8 位（不足 8 位则原样）。</summary>
    public static string ShortId(string? id, int length = 8)
        => string.IsNullOrEmpty(id) ? "—" : id.Length <= length ? id : id[..length];

    /// <summary>把仓库中的 UTC 时间字符串格式化为本地时间。</summary>
    public static string FormatUtcTimestamp(string? utc)
    {
        if (UtcClock.TryParse(utc, out var value))
            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return utc ?? string.Empty;
    }

    /// <summary>字节体积人性化显示。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
}
