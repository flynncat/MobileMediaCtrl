using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaBrowser.Core.Services;

/// <summary>
/// 从文件名中解析日期的工具类。
/// </summary>
public static class FileNameDateParser
{
    // 常见的文件名日期模式：
    // IMG_20240315_123456.jpg / VID_20240315_123456.mp4
    // 20240315_123456.jpg
    // Screenshot_2024-03-15-12-34-56.png
    // PXL_20240315_123456789.jpg (Pixel 手机)
    private static readonly Regex DatePatternCompact = new(
        @"(?:^|[_\-])(?<y>20\d{2})(?<m>0[1-9]|1[0-2])(?<d>0[1-9]|[12]\d|3[01])(?:[_\-](?<H>\d{2})(?<M>\d{2})(?<S>\d{2}))?",
        RegexOptions.Compiled);

    private static readonly Regex DatePatternDashed = new(
        @"(?:^|[_\-])(?<y>20\d{2})-(?<m>0[1-9]|1[0-2])-(?<d>0[1-9]|[12]\d|3[01])",
        RegexOptions.Compiled);

    /// <summary>
    /// 尝试从文件名中解析日期（返回 UTC 时间）。
    /// </summary>
    public static DateTime? TryParse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        // 去掉扩展名
        var nameOnly = Path.GetFileNameWithoutExtension(fileName);

        // 先尝试紧凑格式 20240315
        var m = DatePatternCompact.Match(nameOnly);
        if (m.Success)
            return BuildDateTime(m);

        // 再尝试短横线格式 2024-03-15
        m = DatePatternDashed.Match(nameOnly);
        if (m.Success)
            return BuildDateTime(m);

        return null;
    }

    private static DateTime? BuildDateTime(Match m)
    {
        if (!int.TryParse(m.Groups["y"].Value, out var y) ||
            !int.TryParse(m.Groups["m"].Value, out var mo) ||
            !int.TryParse(m.Groups["d"].Value, out var d))
            return null;

        var hour = 0; var min = 0; var sec = 0;
        if (m.Groups["H"].Success)
        {
            int.TryParse(m.Groups["H"].Value, out hour);
            int.TryParse(m.Groups["M"].Value, out min);
            int.TryParse(m.Groups["S"].Value, out sec);
        }

        try
        {
            var local = new DateTime(y, mo, d, hour, min, sec, DateTimeKind.Local);
            return local.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}
