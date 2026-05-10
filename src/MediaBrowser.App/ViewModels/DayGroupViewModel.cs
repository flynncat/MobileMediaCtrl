using System.Collections.ObjectModel;
using MediaBrowser.App.Infrastructure;

namespace MediaBrowser.App.ViewModels;

public sealed class DayGroupViewModel : ViewModelBase
{
    public DayGroupViewModel(string dateLabel, IEnumerable<MediaTileViewModel> tiles)
    {
        DateLabel = dateLabel;
        Items = new ObservableCollection<MediaTileViewModel>(tiles);
    }

    public string DateLabel { get; }
    public ObservableCollection<MediaTileViewModel> Items { get; }
}
