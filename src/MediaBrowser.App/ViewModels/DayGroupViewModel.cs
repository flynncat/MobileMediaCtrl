using System.Collections.ObjectModel;
using MediaBrowser.App.Infrastructure;

namespace MediaBrowser.App.ViewModels;

/// <summary>
/// 通用媒体分组 ViewModel，可承载按天或按月的分组。
/// 保留 DateLabel / Items 属性名以兼容 XAML 绑定。
/// </summary>
public sealed class MediaGroupViewModel : ViewModelBase
{
    public MediaGroupViewModel(string dateLabel, IEnumerable<MediaTileViewModel> tiles, int year = 0, int month = 0)
    {
        DateLabel = dateLabel;
        Items = new ObservableCollection<MediaTileViewModel>(tiles);
        Year = year;
        Month = month;
    }

    /// <summary>分组标签（如 "2024年03月"）。</summary>
    public string DateLabel { get; }

    /// <summary>分组内的媒体项。</summary>
    public ObservableCollection<MediaTileViewModel> Items { get; }

    /// <summary>分组所属年份（按月分组时使用，用于增量插入定位）。</summary>
    public int Year { get; }

    /// <summary>分组所属月份（按月分组时使用，用于增量插入定位）。</summary>
    public int Month { get; }
}
