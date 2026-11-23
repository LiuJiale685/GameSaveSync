using System.Globalization;

namespace GameSaveSync.Core.Services;

/// <summary>
/// UTC 时间字符串工具。所有仓库文件中的时间统一使用
/// "yyyy-MM-ddTHH:mm:ssZ"（Invariant Culture），保证确定性序列化。
/// </summary>
public static class UtcClock
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string Now()
        => DateTime.UtcNow.ToString(Format, CultureInfo.InvariantCulture);

    public static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out DateTimeOffset result)
        => DateTimeOffset.TryParseExact(
            value,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
}
