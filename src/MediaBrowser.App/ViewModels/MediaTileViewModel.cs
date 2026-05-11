using System.Threading;
using System.Windows.Media.Imaging;
using MediaBrowser.App.Infrastructure;
using MediaBrowser.Core.Models;

namespace MediaBrowser.App.ViewModels;

public sealed class MediaTileViewModel : ViewModelBase
{
    private bool _isSelected;
    private BitmapSource? _thumbnail;
    private CancellationTokenSource? _thumbnailCts;

    public MediaTileViewModel(MediaItem item, string groupKey)
    {
        Item = item;
        GroupKey = groupKey;
    }

    public MediaItem Item { get; }

    /// <summary>分组键（如 "2024年03月"），供 CollectionViewSource 分组使用。</summary>
    public string GroupKey { get; }

    /// <summary>是否为视频文件。</summary>
    public bool IsVideo => Item.IsVideo;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>获取或创建用于缩略图加载的 CancellationTokenSource。</summary>
    public CancellationTokenSource GetOrCreateCts()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        return _thumbnailCts;
    }

    /// <summary>取消正在进行的缩略图加载。</summary>
    public void CancelThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }
}
