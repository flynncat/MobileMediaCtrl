using System.Windows.Media.Imaging;
using MediaBrowser.App.Infrastructure;
using MediaBrowser.Core.Models;

namespace MediaBrowser.App.ViewModels;

public sealed class MediaTileViewModel : ViewModelBase
{
    private bool _isSelected;
    private BitmapSource? _thumbnail;

    public MediaTileViewModel(MediaItem item) => Item = item;

    public MediaItem Item { get; }

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
}
