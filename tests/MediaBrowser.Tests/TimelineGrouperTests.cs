using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;

namespace MediaBrowser.Tests;

public class TimelineGrouperTests
{
    [Fact]
    public void GroupByLocalDay_Orders_Newest_Day_First()
    {
        var tz = TimeZoneInfo.Utc;
        var day1 = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2024, 5, 10, 8, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new MediaItem { Id = "a", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = day1, DisplayName = "a" },
            new MediaItem { Id = "b", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = day2, DisplayName = "b" },
        };

        var groups = TimelineGrouper.GroupByLocalDay(items, tz);

        Assert.Equal(2, groups.Count);
        Assert.Equal(DateOnly.FromDateTime(day2), groups[0].DateLocal);
        Assert.Equal(DateOnly.FromDateTime(day1), groups[1].DateLocal);
    }
}
