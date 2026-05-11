using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

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
    private readonly SemaphoreSlim _thumbGate = new(4);
    private readonly Dispatcher _dispatcher;
    private MediaDevice? _mtpDevice;
    private int _totalItemCount;
    /// <summary>当前连接的MTP设备（预览时需要用来下载文件）。</summary>
    public MediaDevice? MtpDevice => _mtpDevice;


    private string _currentDropTargetPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private string _statusText = LanguageManager.GetString("VM_Loading");

    private bool _isBusy;

    public MediaWindowViewModel(DeviceSessionDescriptor descriptor)
    {
        _descriptor = descriptor;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Title = descriptor.DisplayName;
        BrowseTargetFolderCommand = new RelayCommand(_ => BrowseTargetFolder());
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        CopySelectedToTargetCommand = new RelayCommand(_ => _ = CopySelectedToTargetAsync(), _ => !IsBusy);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll());
    }


    public string Title { get; }

    public ObservableCollection<MediaGroupViewModel> DayGroups { get; } = new();


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
        StatusText = LanguageManager.GetString("VM_Loading");
        _totalItemCount = 0;

        DayGroups.Clear();

        try
        {
            if (_descriptor.Kind == DeviceKind.RemovableVolume)
            {
                _mtpDevice = null;
                var root = _descriptor.VolumeRootPath!;
                var catalog = new FileSystemMediaCatalog();
                var items = await catalog.EnumerateAsync(root).ConfigureAwait(true);

                // 文件系统：一次性分组显示，然后异步加载缩略图
                AddItemsToGroups(items);
                _totalItemCount = items.Count;
                StatusText = LanguageManager.GetString("VM_MediaCount", _totalItemCount);
                _ = LoadThumbnailsForAsync(AllTiles().ToList());
            }
            else
            {
                _mtpDevice?.Disconnect();
                _mtpDevice = MtpDeviceLister.TryConnectByName(_descriptor.MtpDeviceId!);
                if (_mtpDevice is null)
                {
                    StatusText = LanguageManager.GetString("VM_DeviceNotFound");
                    return;
                }

                // MTP：使用增量批次回调，边扫描边显示
                var progress = new Progress<MtpScanProgress>(p => StatusText = p.Message);
                Action<IReadOnlyList<MediaItem>> batchCallback = batch =>
                {
                    // 批次回调在后台线程，需要 Dispatch 到 UI 线程
                    _dispatcher.Invoke(() =>
                    {
                        var newTiles = AddItemsToGroups(batch);
                        _totalItemCount += batch.Count;
                        // 立即为这批新文件启动缩略图加载
                        _ = LoadThumbnailsForAsync(newTiles);
                    });
                };

                var items = await MtpMediaCatalog.EnumerateAsync(
                    _mtpDevice, progress, batchCallback).ConfigureAwait(true);

                _totalItemCount = items.Count;
                StatusText = LanguageManager.GetString("VM_MediaCount", _totalItemCount);
            }
        }
        catch (Exception ex)
        {
            StatusText = LanguageManager.GetString("VM_LoadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 将一批 MediaItem 增量插入到已有的月份分组中。
    /// 若对应月份组已存在则追加，否则创建新组并插入到正确的排序位置。
    /// 返回新创建的 MediaTileViewModel 列表（用于启动缩略图加载）。
    /// </summary>
    private List<MediaTileViewModel> AddItemsToGroups(IEnumerable<MediaItem> items)
    {
        Func<int, int, string> labelFormatter = (y, m) =>
            LanguageManager.GetString("Group_MonthFormat", y, m);

        var newTiles = new List<MediaTileViewModel>();

        foreach (var item in items)
        {
            var (year, month) = TimelineGrouper.GetMonthKey(item);
            var tile = new MediaTileViewModel(item);
            newTiles.Add(tile);

            // 查找已有的月份组
            var group = DayGroups.FirstOrDefault(g => g.Year == year && g.Month == month);
            if (group is not null)
            {
                // 插入到组内正确的排序位置（从新到旧）
                var insertIdx = 0;
                while (insertIdx < group.Items.Count &&
                       group.Items[insertIdx].Item.SortTimeUtc >= item.SortTimeUtc)
                    insertIdx++;
                group.Items.Insert(insertIdx, tile);
            }
            else
            {
                // 创建新组并插入到正确的排序位置（从新到旧）
                var newGroup = new MediaGroupViewModel(
                    labelFormatter(year, month),
                    new[] { tile },
                    year, month);

                var groupIdx = 0;
                while (groupIdx < DayGroups.Count)
                {
                    var existing = DayGroups[groupIdx];
                    if (existing.Year < year || (existing.Year == year && existing.Month < month))
                        break;
                    groupIdx++;
                }
                DayGroups.Insert(groupIdx, newGroup);
            }
        }

        return newTiles;
    }


    /// <summary>
    /// 为指定的 tile 列表加载缩略图，使用共享的并发控制。
    /// </summary>
    private async Task LoadThumbnailsForAsync(IReadOnlyList<MediaTileViewModel> tiles)
    {
        var token = CancellationToken.None;
        var tasks = tiles.Select(async tile =>
        {
            await _thumbGate.WaitAsync(token).ConfigureAwait(true);
            try
            {
                var bmp = await _thumbs.LoadAsync(tile.Item, _mtpDevice, cancellationToken: token)
                    .ConfigureAwait(true);
                if (bmp is not null)
                    tile.Thumbnail = bmp;
            }
            finally
            {
                _thumbGate.Release();
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
            Description = LanguageManager.GetString("VM_BrowseFolder"),

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
            System.Windows.MessageBox.Show(LanguageManager.GetString("VM_PleaseSelect"), LanguageManager.GetString("VM_Hint"), MessageBoxButton.OK, MessageBoxImage.Information);


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

            var msg = LanguageManager.GetString("VM_CopyDone", result.SuccessCount, result.SkippedCount, result.FailedCount);

            if (result.Errors.Count > 0)
                msg += "\n" + string.Join("\n", result.Errors.Take(5));
            System.Windows.MessageBox.Show(msg, LanguageManager.GetString("VM_CopyResult"), MessageBoxButton.OK,

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
            sourceDevice = string.Equals(pnp, _descriptor.MtpDeviceId, StringComparison.OrdinalIgnoreCase)
                ? _mtpDevice
                : MtpDeviceLister.TryConnectByName(pnp);

        }

        IsBusy = true;
        try
        {
            var result = await PortableTransferService.CopyToDirectoryAsync(
                items,
                CurrentDropTargetPath,
                sourceDevice,
                new CopyOptions { CollisionPolicy = NameCollisionPolicy.AutoRename }).ConfigureAwait(true);

            if (sourceDevice is not null && sourceDevice != _mtpDevice)
                sourceDevice.Disconnect();


            System.Windows.MessageBox.Show(
                LanguageManager.GetString("VM_CopiedToTarget", CurrentDropTargetPath, result.SuccessCount, result.SkippedCount, result.FailedCount),
                LanguageManager.GetString("VM_CopyResult"),

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

            var dev = string.Equals(r.PnpId, _descriptor.MtpDeviceId, StringComparison.OrdinalIgnoreCase)
                ? _mtpDevice
                : MtpDeviceLister.TryConnectByName(r.PnpId);
            if (dev is null)
                continue;


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

            if (dev != _mtpDevice)
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
