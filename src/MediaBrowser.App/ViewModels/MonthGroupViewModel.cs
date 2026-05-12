using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MediaBrowser.App.Infrastructure;

namespace MediaBrowser.App.ViewModels;

/// <summary>
/// 月份分组 ViewModel，包含该月份下的所有媒体项。
/// </summary>
public sealed class MonthGroupViewModel : ViewModelBase
{
    private bool _isExpanded = true;

    public MonthGroupViewModel(int year, int month)
    {
        Year = year;
        Month = month;
        Items = new ObservableCollection<MediaTileViewModel>();
        Items.CollectionChanged += OnItemsChanged;
    }

    public int Year { get; }
    public int Month { get; }

    /// <summary>是否展开（默认展开）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>该月份下的媒体项集合（按时间从新到旧排序）。</summary>
    public ObservableCollection<MediaTileViewModel> Items { get; }

    /// <summary>显示标签，如 "3月 (42)"。</summary>
    public string DisplayLabel => $"{Month}月 ({Items.Count})";

    /// <summary>用于排序的键（年月组合）。</summary>
    public int SortKey => Year * 100 + Month;

    public void RefreshDisplayLabel()
    {
        OnPropertyChanged(nameof(DisplayLabel));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayLabel();
    }
}
