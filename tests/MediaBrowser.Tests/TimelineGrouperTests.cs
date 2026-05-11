using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;
using Xunit;

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

    // ===== GroupByLocalMonth 测试 =====

    [Fact]
    public void GroupByLocalMonth_Groups_By_Month_And_Orders_Newest_First()
    {
        var tz = TimeZoneInfo.Utc;
        var jan = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var mar1 = new DateTime(2024, 3, 5, 8, 0, 0, DateTimeKind.Utc);
        var mar2 = new DateTime(2024, 3, 20, 14, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new MediaItem { Id = "a", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = jan, DisplayName = "a" },
            new MediaItem { Id = "b", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = mar1, DisplayName = "b" },
            new MediaItem { Id = "c", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = mar2, DisplayName = "c" },
        };

        var groups = TimelineGrouper.GroupByLocalMonth(items, tz: tz);

        // 应有 2 个月份组：3月和1月
        Assert.Equal(2, groups.Count);
        Assert.Equal(2024, groups[0].Year);
        Assert.Equal(3, groups[0].Month);
        Assert.Equal(2024, groups[1].Year);
        Assert.Equal(1, groups[1].Month);
    }

    [Fact]
    public void GroupByLocalMonth_Items_Within_Group_Ordered_Newest_First()
    {
        var tz = TimeZoneInfo.Utc;
        var early = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var mid = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2024, 6, 28, 20, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new MediaItem { Id = "a", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = early, DisplayName = "a" },
            new MediaItem { Id = "b", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = late, DisplayName = "b" },
            new MediaItem { Id = "c", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = mid, DisplayName = "c" },
        };

        var groups = TimelineGrouper.GroupByLocalMonth(items, tz: tz);

        Assert.Single(groups);
        // 组内按时间从新到旧：late > mid > early
        Assert.Equal("b", groups[0].Items[0].DisplayName);
        Assert.Equal("c", groups[0].Items[1].DisplayName);
        Assert.Equal("a", groups[0].Items[2].DisplayName);
    }

    [Fact]
    public void GroupByLocalMonth_Uses_Custom_LabelFormatter()
    {
        var tz = TimeZoneInfo.Utc;
        var items = new[]
        {
            new MediaItem { Id = "a", SourceKind = MediaSourceKind.FileSystem,
                SortTimeUtc = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc), DisplayName = "a" },
        };

        var groups = TimelineGrouper.GroupByLocalMonth(items,
            labelFormatter: (y, m) => $"{y}年{m:D2}月", tz: tz);

        Assert.Single(groups);
        Assert.Equal("2024年03月", groups[0].DateLabel);
    }

    [Fact]
    public void GroupByLocalMonth_Cross_Year_Groups_Correctly()
    {
        var tz = TimeZoneInfo.Utc;
        var dec2023 = new DateTime(2023, 12, 25, 0, 0, 0, DateTimeKind.Utc);
        var jan2024 = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new MediaItem { Id = "a", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = dec2023, DisplayName = "a" },
            new MediaItem { Id = "b", SourceKind = MediaSourceKind.FileSystem, SortTimeUtc = jan2024, DisplayName = "b" },
        };

        var groups = TimelineGrouper.GroupByLocalMonth(items, tz: tz);

        Assert.Equal(2, groups.Count);
        // 最新的月份在前
        Assert.Equal(2024, groups[0].Year);
        Assert.Equal(1, groups[0].Month);
        Assert.Equal(2023, groups[1].Year);
        Assert.Equal(12, groups[1].Month);
    }

    [Fact]
    public void GetMonthKey_Returns_Correct_Year_Month()
    {
        var tz = TimeZoneInfo.Utc;
        var item = new MediaItem
        {
            Id = "x", SourceKind = MediaSourceKind.FileSystem,
            SortTimeUtc = new DateTime(2024, 7, 20, 15, 30, 0, DateTimeKind.Utc),
            DisplayName = "x"
        };

        var (year, month) = TimelineGrouper.GetMonthKey(item, tz);

        Assert.Equal(2024, year);
        Assert.Equal(7, month);
    }
}
