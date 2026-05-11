using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;

using MediaBrowser.App.Infrastructure;
using MediaBrowser.App.Services;
using MediaBrowser.Core.Catalog;
using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;
using MediaDevices;

namespace MediaBrowser.App.ViewModels;

public sealed class MediaWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DeviceSessionDescriptor _descriptor;
    private readonly ThumbnailLoader _thumbs = new();
    private MediaDevice? _mtpDevice;
    private string _currentDropTargetPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private string _statusText = "正在加载…";
    private bool _isBusy;

    public MediaWindowViewModel(DeviceSessionDescriptor descriptor)
    {
        _descriptor = descriptor;
        Title = descriptor.DisplayName;
        BrowseTargetFolderCommand = new RelayCommand(_ => BrowseTargetFolder());
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        CopySelectedToTargetCommand = new RelayCommand(_ => _ = CopySelectedToTargetAsync(), _ => !IsBusy);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll());
    }

    public string Title { get; }

    public ObservableCollection<DayGroupViewModel> DayGroups { get; } = new();

    public string CurrentDropTargetPath
    {
        get => _currentDropTargetPath;
        set => SetProperty(ref _currentDropTargetPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand BrowseTargetFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CopySelectedToTargetCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "正在加载…";
        DayGroups.Clear();

        try
        {
            IReadOnlyList<MediaItem> items;
            if (_descriptor.Kind == DeviceKind.RemovableVolume)
            {
                _mtpDevice = null;
                var root = _descriptor.VolumeRootPath!;
                var catalog = new FileSystemMediaCatalog();
                items = await catalog.EnumerateAsync(root).ConfigureAwait(true);
            }
            else
            {
                _mtpDevice?.Disconnect();
                _mtpDevice = MtpDeviceLister.TryFindByPnPId(_descriptor.MtpDeviceId!);
                if (_mtpDevice is null)
                {
                    StatusText = "未找到手机/便携设备，请重新连接。";
                    return;
                }

                _mtpDevice.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);
                items = await MtpMediaCatalog.EnumerateAsync(_mtpDevice).ConfigureAwait(true);
            }

            var groups = TimelineGrouper.GroupByLocalDay(items);
            foreach (var g in groups)
            {
                var tiles = g.Items.Select(i => new MediaTileViewModel(i)).ToList();
                DayGroups.Add(new DayGroupViewModel(g.DateLabel, tiles));
            }

            StatusText = $"共 {items.Count} 个媒体文件。路径栏为复制目标。";
            _ = LoadThumbnailsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        using var gate = new SemaphoreSlim(4);
        var token = CancellationToken.None;
        var tasks = AllTiles().Select(async tile =>
        {
            await gate.WaitAsync(token).ConfigureAwait(true);
            try
            {
                var bmp = await _thumbs.LoadAsync(tile.Item, _mtpDevice, cancellationToken: token)
                    .ConfigureAwait(true);
                if (bmp is not null)
                    tile.Thumbnail = bmp;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private IEnumerable<MediaTileViewModel> AllTiles() =>
        DayGroups.SelectMany(d => d.Items);

    private void BrowseTargetFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择复制目标文件夹",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            CurrentDropTargetPath = dlg.SelectedPath;
    }

    private async Task CopySelectedToTargetAsync()
    {
        var selected = AllTiles().Where(t => t.IsSelected).Select(t => t.Item).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("请先勾选要复制的文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        IsBusy = true;
        try
        {
            var result = await PortableTransferService.CopyToDirectoryAsync(
                selected,
                CurrentDropTargetPath,
                _mtpDevice,
                new CopyOptions { CollisionPolicy = NameCollisionPolicy.AutoRename }).ConfigureAwait(true);

            var msg =
                $"完成：成功 {result.SuccessCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}。";
            if (result.Errors.Count > 0)
                msg += "\n" + string.Join("\n", result.Errors.Take(5));
            System.Windows.MessageBox.Show(msg, "复制结果", MessageBoxButton.OK,
                result.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ToggleSelectAll()
    {
        var tiles = AllTiles().ToList();
        if (tiles.Count == 0)
            return;
        var allOn = tiles.All(t => t.IsSelected);
        foreach (var t in tiles)
            t.IsSelected = !allOn;
    }

    public IReadOnlyList<MediaDragRecord> BuildDragRecordsForSelection()
    {
        return AllTiles()
            .Where(t => t.IsSelected)
            .Select(t => ToRecord(t.Item))
            .ToList();
    }

    public IReadOnlyList<MediaDragRecord> BuildDragRecords(IEnumerable<MediaItem> items) =>
        items.Select(ToRecord).ToList();

    private static MediaDragRecord ToRecord(MediaItem item)
    {
        if (item.SourceKind == MediaSourceKind.FileSystem)
        {
            return new MediaDragRecord
            {
                Kind = "fs",
                FsPath = item.FileSystemPath,
                DisplayName = item.DisplayName,
            };
        }

        return new MediaDragRecord
        {
            Kind = "mtp",
            PnpId = item.MtpDeviceId,
            MtpPath = item.MtpObjectId,
            DisplayName = item.DisplayName,
        };
    }

    public async Task HandleInternalDropAsync(string jsonPayload)
    {
        List<MediaDragRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<MediaDragRecord>>(jsonPayload, MediaDragJson.Options);
        }
        catch
        {
            return;
        }

        if (records is null || records.Count == 0)
            return;

        var items = new List<MediaItem>();
        foreach (var r in records)
        {
            if (r.Kind == "fs" && !string.IsNullOrEmpty(r.FsPath))
            {
                items.Add(new MediaItem
                {
                    Id = r.FsPath,
                    SourceKind = MediaSourceKind.FileSystem,
                    FileSystemPath = r.FsPath,
                    DisplayName = r.DisplayName,
                    SortTimeUtc = DateTime.UtcNow,
                });
            }
            else if (r.Kind == "mtp" && !string.IsNullOrEmpty(r.PnpId) && !string.IsNullOrEmpty(r.MtpPath))
            {
                items.Add(new MediaItem
                {
                    Id = $"mtp:{r.PnpId}|{r.MtpPath}",
                    SourceKind = MediaSourceKind.Mtp,
                    DisplayName = r.DisplayName,
                    MtpDeviceId = r.PnpId,
                    MtpObjectId = r.MtpPath,
                    SortTimeUtc = DateTime.UtcNow,
                });
            }
        }

        MediaDevice? sourceDevice = null;
        var mtpRecords = records.Where(x => x.Kind == "mtp").ToList();
        if (mtpRecords.Count > 0)
        {
            var pnp = mtpRecords[0].PnpId!;
            sourceDevice = string.Equals(pnp, _descriptor.MtpDeviceId, StringComparison.Ordinal)
                ? _mtpDevice
                : MtpDeviceLister.TryFindByPnPId(pnp);
            sourceDevice?.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);
        }

        IsBusy = true;
        try
        {
            var result = await PortableTransferService.CopyToDirectoryAsync(
                items,
                CurrentDropTargetPath,
                sourceDevice,
                new CopyOptions { CollisionPolicy = NameCollisionPolicy.AutoRename }).ConfigureAwait(true);

            if (sourceDevice is not null &&
                !string.Equals(sourceDevice.PnPDeviceID, _descriptor.MtpDeviceId, StringComparison.Ordinal))
                sourceDevice.Disconnect();

            System.Windows.MessageBox.Show(
                $"已复制到当前路径栏目录：\n{CurrentDropTargetPath}\n成功 {result.SuccessCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}。",
                "复制结果",
                MessageBoxButton.OK,
                result.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<IReadOnlyList<string>> StageFilesForShellDragAsync(IReadOnlyList<MediaDragRecord> records)
    {
        var paths = new List<string>();
        var tempRoot = Path.Combine(Path.GetTempPath(), "MediaBrowserDrag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        foreach (var r in records)
        {
            if (r.Kind == "fs" && File.Exists(r.FsPath))
            {
                paths.Add(r.FsPath!);
                continue;
            }

            if (r.Kind != "mtp" || string.IsNullOrEmpty(r.MtpPath) || string.IsNullOrEmpty(r.PnpId))
                continue;

            var dev = string.Equals(r.PnpId, _descriptor.MtpDeviceId, StringComparison.Ordinal)
                ? _mtpDevice
                : MtpDeviceLister.TryFindByPnPId(r.PnpId);
            if (dev is null)
                continue;
            if (!dev.IsConnected)
                dev.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);

            var name = string.IsNullOrWhiteSpace(r.DisplayName)
                ? Path.GetFileName(r.MtpPath.TrimEnd('\\'))
                : r.DisplayName;
            var dest = Path.Combine(tempRoot, name);
            try
            {
                await using (var fs = File.Create(dest))
                {
                    await Task.Run(() => dev.DownloadFile(r.MtpPath!, fs)).ConfigureAwait(true);
                }

                paths.Add(dest);
            }
            catch
            {
                // ignore single failure
            }

            if (!string.Equals(dev.PnPDeviceID, _descriptor.MtpDeviceId, StringComparison.Ordinal))
                dev.Disconnect();
        }

        return paths;
    }

    public void Dispose()
    {
        try
        {
            _mtpDevice?.Disconnect();
        }
        catch
        {
            // ignore
        }
    }
}
