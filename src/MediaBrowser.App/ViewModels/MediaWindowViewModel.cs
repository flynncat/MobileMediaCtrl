using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Data;
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

    // ── MTP 批次节流 ──
    private readonly object _batchLock = new();
    private List<MediaItem>? _pendingBatch;
    private bool _batchScheduled;

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
        OpenExportFolderCommand = new RelayCommand(_ => OpenExportFolder());


        // 配置 CollectionViewSource 分组
        var cvs = new CollectionViewSource { Source = Tiles };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MediaTileViewModel.GroupKey)));
        GroupedView = cvs.View;
    }

    public string Title { get; }

    /// <summary>扁平化的媒体 tile 集合。</summary>
    public ObservableCollection<MediaTileViewModel> Tiles { get; } = new();

    /// <summary>带分组的视图，供 XAML 绑定。</summary>
    public System.ComponentModel.ICollectionView GroupedView { get; }

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
    public ICommand OpenExportFolderCommand { get; }


    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = LanguageManager.GetString("VM_Loading");
        _totalItemCount = 0;

        Tiles.Clear();

        try
        {
            if (_descriptor.Kind == DeviceKind.RemovableVolume)
            {
                _mtpDevice = null;
                var root = _descriptor.VolumeRootPath!;
                var catalog = new FileSystemMediaCatalog();
                var items = await catalog.EnumerateAsync(root).ConfigureAwait(true);

                // 文件系统：一次性分组显示
                AddItemsToFlatList(items);
                _totalItemCount = items.Count;
                StatusText = LanguageManager.GetString("VM_MediaCount", _totalItemCount);
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
                    // 批次节流：累积多个小批次后合并更新 UI
                    lock (_batchLock)
                    {
                        _pendingBatch ??= new List<MediaItem>();
                        _pendingBatch.AddRange(batch);

                        if (!_batchScheduled)
                        {
                            _batchScheduled = true;
                            // 延迟 100ms 合并批次
                            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                            {
                                List<MediaItem>? toProcess;
                                lock (_batchLock)
                                {
                                    toProcess = _pendingBatch;
                                    _pendingBatch = null;
                                    _batchScheduled = false;
                                }

                                if (toProcess is null or { Count: 0 })
                                    return;

                                AddItemsToFlatList(toProcess);
                                _totalItemCount += toProcess.Count;
                                StatusText = LanguageManager.GetString("VM_Scanning", _totalItemCount);
                            });

                        }
                    }
                };

                var items = await MtpMediaCatalog.EnumerateAsync(
                    _mtpDevice, progress, batchCallback).ConfigureAwait(true);

                // 最终刷新：确保所有剩余批次都已处理
                List<MediaItem>? remaining;
                lock (_batchLock)
                {
                    remaining = _pendingBatch;
                    _pendingBatch = null;
                    _batchScheduled = false;
                }
                if (remaining is { Count: > 0 })
                {
                    AddItemsToFlatList(remaining);
                    _totalItemCount += remaining.Count;
                }

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
    /// 将一批 MediaItem 增量插入到扁平集合中，按月份分组、时间从新到旧排序。
    /// </summary>
    private void AddItemsToFlatList(IEnumerable<MediaItem> items)
    {
        if (items is null)
            return;

        Func<int, int, string> labelFormatter = (y, m) =>
            LanguageManager.GetString("Group_MonthFormat", y, m);


        foreach (var item in items)
        {
            var (year, month) = TimelineGrouper.GetMonthKey(item);
            var groupKey = labelFormatter(year, month);
            var tile = new MediaTileViewModel(item, groupKey);

            // 找到正确的插入位置（按时间从新到旧）
            var insertIdx = FindInsertIndex(tile);
            Tiles.Insert(insertIdx, tile);
        }
    }

    /// <summary>
    /// 在扁平集合中找到正确的插入位置，保持按月份分组、组内按时间从新到旧排序。
    /// </summary>
    private int FindInsertIndex(MediaTileViewModel newTile)
    {
        var newTime = newTile.Item.SortTimeUtc;
        var newKey = newTile.GroupKey;

        for (int i = 0; i < Tiles.Count; i++)
        {
            var existing = Tiles[i];
            // 先按分组键排序（月份从新到旧），再按时间从新到旧
            if (existing.GroupKey == newKey)
            {
                // 同组内，找到第一个比新项更旧的位置
                if (existing.Item.SortTimeUtc < newTime)
                    return i;
            }
            else
            {
                // 不同组：如果当前组的时间比新项旧，说明新项应该在这之前
                if (existing.Item.SortTimeUtc < newTime)
                    return i;
            }
        }

        return Tiles.Count;
    }

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

    private void OpenExportFolder()
    {
        var path = CurrentDropTargetPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            System.Windows.MessageBox.Show(
                LanguageManager.GetString("VM_ExportFolderNotExist"),
                LanguageManager.GetString("VM_Hint"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                LanguageManager.GetString("VM_OpenFolderFailed", ex.Message),
                LanguageManager.GetString("VM_Hint"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private async Task CopySelectedToTargetAsync()
    {
        var selected = Tiles.Where(t => t.IsSelected).Select(t => t.Item).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(
                LanguageManager.GetString("VM_PleaseSelect"),
                LanguageManager.GetString("VM_Hint"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (Tiles.Count == 0)
            return;
        var allOn = Tiles.All(t => t.IsSelected);
        foreach (var t in Tiles)
            t.IsSelected = !allOn;
    }

    public IReadOnlyList<MediaDragRecord> BuildDragRecordsForSelection()
    {
        return Tiles
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

    /// <summary>
    /// 为文件系统设备快速构建拖拽路径列表（无需下载，零延迟）。
    /// </summary>
    public IReadOnlyList<string> BuildShellDragPathsForFileSystem(IReadOnlyList<MediaDragRecord> records)
    {
        var paths = new List<string>();
        foreach (var r in records)
        {
            if (r.Kind == "fs" && !string.IsNullOrEmpty(r.FsPath) && File.Exists(r.FsPath))
                paths.Add(r.FsPath!);
        }
        return paths;
    }

    /// <summary>
    /// 为MTP设备下载文件到临时目录以支持Shell拖拽。
    /// 在后台线程执行以避免阻塞UI。
    /// </summary>
    public async Task<IReadOnlyList<string>> StageFilesForShellDragAsync(IReadOnlyList<MediaDragRecord> records)
    {
        var paths = new List<string>();
        var tempRoot = Path.Combine(Path.GetTempPath(), "MediaBrowserDrag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        foreach (var r in records)
        {
            if (r.Kind == "fs" && !string.IsNullOrEmpty(r.FsPath) && File.Exists(r.FsPath))
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
                // 忽略单个文件失败
            }

            if (dev != _mtpDevice)
                dev.Disconnect();
        }

        return paths;
    }

    /// <summary>
    /// 判断当前设备是否为文件系统设备（可直接使用文件路径拖拽）。
    /// </summary>
    public bool IsFileSystemDevice => _descriptor.Kind == DeviceKind.RemovableVolume;


    /// <summary>
    /// 缩略图加载器和并发控制门，供懒加载行为使用。
    /// </summary>
    public ThumbnailLoader ThumbLoader => _thumbs;
    public SemaphoreSlim ThumbGate => _thumbGate;

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
