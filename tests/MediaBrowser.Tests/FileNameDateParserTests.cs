using MediaBrowser.Core.Services;
using Xunit;

namespace MediaBrowser.Tests;

public class FileNameDateParserTests
{
    [Theory]
    [InlineData("IMG_20240315_123456.jpg", 2024, 3, 15)]
    [InlineData("VID_20240315_093000.mp4", 2024, 3, 15)]
    [InlineData("PXL_20240315_123456789.jpg", 2024, 3, 15)]
    [InlineData("20240315_123456.jpg", 2024, 3, 15)]
    public void TryParse_CompactFormat_ReturnsCorrectDate(string fileName, int year, int month, int day)
    {
        var result = FileNameDateParser.TryParse(fileName);

        Assert.NotNull(result);
        var local = result.Value.ToLocalTime();
        Assert.Equal(year, local.Year);
        Assert.Equal(month, local.Month);
        Assert.Equal(day, local.Day);
    }

    [Theory]
    [InlineData("Screenshot_2024-03-15-12-34-56.png", 2024, 3, 15)]
    [InlineData("photo_2024-01-20.jpg", 2024, 1, 20)]
    public void TryParse_DashedFormat_ReturnsCorrectDate(string fileName, int year, int month, int day)
    {
        var result = FileNameDateParser.TryParse(fileName);

        Assert.NotNull(result);
        var local = result.Value.ToLocalTime();
        Assert.Equal(year, local.Year);
        Assert.Equal(month, local.Month);
        Assert.Equal(day, local.Day);
    }

    [Theory]
    [InlineData("IMG_20240315_123456.jpg", 12, 34, 56)]
    [InlineData("VID_20240315_093000.mp4", 9, 30, 0)]
    public void TryParse_CompactFormat_IncludesTime(string fileName, int hour, int min, int sec)
    {
        var result = FileNameDateParser.TryParse(fileName);

        Assert.NotNull(result);
        var local = result.Value.ToLocalTime();
        Assert.Equal(hour, local.Hour);
        Assert.Equal(min, local.Minute);
        Assert.Equal(sec, local.Second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("random_file.jpg")]
    [InlineData("notes.txt")]
    [InlineData("document_v2.pdf")]
    [InlineData("IMG_abcdefgh.jpg")]
    public void TryParse_InvalidOrNoDate_ReturnsNull(string? fileName)
    {
        var result = FileNameDateParser.TryParse(fileName!);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_InvalidMonth13_ReturnsNull()
    {
        // 月份 13 不合法
        var result = FileNameDateParser.TryParse("IMG_20241315_120000.jpg");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_InvalidDay32_ReturnsNull()
    {
        // 日期 32 不合法
        var result = FileNameDateParser.TryParse("IMG_20240332_120000.jpg");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_DateOnlyWithoutTime_ReturnsDateAtMidnight()
    {
        // 紧凑格式无时间部分，如 PXL_20240315_123456789（时间部分是9位，不匹配6位模式）
        // 这里测试纯日期无时间的情况
        var result = FileNameDateParser.TryParse("photo-20240601.jpg");

        Assert.NotNull(result);
        var local = result.Value.ToLocalTime();
        Assert.Equal(2024, local.Year);
        Assert.Equal(6, local.Month);
        Assert.Equal(1, local.Day);
        Assert.Equal(0, local.Hour);
        Assert.Equal(0, local.Minute);
    }
}
