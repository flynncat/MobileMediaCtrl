using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MediaBrowser.App.Infrastructure;

namespace MediaBrowser.App.ViewModels;

/// <summary>
/// 年份分组 ViewModel，包含该年份下的所有月份分组。
/// </summary>
public sealed class YearGroupViewModel : ViewModelBase
{
    private bool _isExpanded = true;

    public YearGroupViewModel(int year)
    {
        Year = year;
        MonthGroups = new ObservableCollection<MonthGroupViewModel>();
        MonthGroups.CollectionChanged += OnMonthGroupsChanged;
    }

    public int Year { get; }

    /// <summary>是否展开（默认展开）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>该年份下的月份分组集合（按月份从新到旧排序）。</summary>
    public ObservableCollection<MonthGroupViewModel> MonthGroups { get; }

    /// <summary>该年份下的媒体总数。</summary>
    public int TotalCount => MonthGroups.Sum(m => m.Items.Count);

    /// <summary>显示标签，如 "2024年 (128)"。</summary>
    public string DisplayLabel => $"{Year}年 ({TotalCount})";

    public void RefreshDisplayLabel()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(DisplayLabel));
    }

    private void OnMonthGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayLabel();
    }
}
