using MediaBrowser.Core.Models;

namespace MediaBrowser.Core.Services;

public sealed class DayGroup
{
    public DateOnly DateLocal { get; init; }
    public string DateLabel { get; init; } = "";
    public IReadOnlyList<MediaItem> Items { get; init; } = Array.Empty<MediaItem>();
}

public sealed class MonthGroup
{
    /// <summary>该月份的第一天（用于排序）。</summary>
    public DateOnly MonthStart { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public string DateLabel { get; set; } = "";
    public IReadOnlyList<MediaItem> Items { get; init; } = Array.Empty<MediaItem>();
}

public static class TimelineGrouper
{
    public static IReadOnlyList<DayGroup> GroupByLocalDay(IEnumerable<MediaItem> items, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var ordered = items
            .OrderByDescending(i => i.SortTimeUtc)
            .ToList();

        return ordered
            .GroupBy(i => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(i.SortTimeUtc, tz)))
            .Select(g => new DayGroup
            {
                DateLocal = g.Key,
                DateLabel = g.Key.ToString("yyyy-MM-dd dddd"),
                Items = g.OrderByDescending(x => x.SortTimeUtc).ToList(),
            })
            .OrderByDescending(g => g.DateLocal)
            .ToList();
    }

    /// <summary>
    /// 按本地月份分组，组内和组间均按时间从新到旧排列。
    /// <paramref name="labelFormatter"/> 可选，用于自定义分组标签格式（传入 year, month）。
    /// 若为 null，则使用默认格式 "yyyy-MM"。
    /// </summary>
    public static IReadOnlyList<MonthGroup> GroupByLocalMonth(
        IEnumerable<MediaItem> items,
        Func<int, int, string>? labelFormatter = null,
        TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        labelFormatter ??= (y, m) => $"{y}-{m:D2}";

        return items
            .OrderByDescending(i => i.SortTimeUtc)
            .GroupBy(i =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(i.SortTimeUtc, tz);
                return (local.Year, local.Month);
            })
            .Select(g => new MonthGroup
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                MonthStart = new DateOnly(g.Key.Year, g.Key.Month, 1),
                DateLabel = labelFormatter(g.Key.Year, g.Key.Month),
                Items = g.OrderByDescending(x => x.SortTimeUtc).ToList(),
            })
            .OrderByDescending(g => g.MonthStart)
            .ToList();
    }

    /// <summary>
    /// 将单个 MediaItem 归入月份键（year, month），用于增量分组。
    /// </summary>
    public static (int Year, int Month) GetMonthKey(MediaItem item, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(item.SortTimeUtc, tz);
        return (local.Year, local.Month);
    }
}
