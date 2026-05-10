using MediaBrowser.Core.Models;

namespace MediaBrowser.Core.Services;

public sealed class DayGroup
{
    public DateOnly DateLocal { get; init; }
    public string DateLabel { get; init; } = "";
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
}
