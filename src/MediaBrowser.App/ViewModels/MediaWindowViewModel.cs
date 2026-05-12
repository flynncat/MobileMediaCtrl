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

    // ── MTP 批次节流 ──
    private readonly object _batchLock = new();
    private List<MediaItem>? _pendingBatch;
    private bool _batchScheduled;

    /// <summary>当前连接的MTP设备（预览时需要用来下载文件）。</summary>
    public MediaDevice? MtpDevice => _mtpDevice;

    /// <summary>设备的 PnP ID（MTP）或卷路径（文件系统），用于拖放时识别同源设备。</summary>
    public string? DeviceId => _descriptor.MtpDeviceId ?? _descriptor.VolumeRootPath;


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
    }


    public string Title { get; }

    /// <summary>扁平化的媒体 tile 集合（保留用于全选/拖拽等操作的快速访问）。</summary>
    public ObservableCollection<MediaTileViewModel> Tiles { get; } = new();

    /// <summary>两级分组集合：年→月→媒体项，供 XAML 绑定。</summary>
    public ObservableCollection<YearGroupViewModel> YearGroups { get; } = new();


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
        YearGroups.Clear();


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
    /// 将一批 MediaItem 增量插入到两级分组集合中，按年月分组、时间从新到旧排序。
    /// </summary>
    private void AddItemsToFlatList(IEnumerable<MediaItem> items)
    {
        if (items is null)
            return;

        foreach (var item in items)
        {
            var (year, month) = TimelineGrouper.GetMonthKey(item);
            var groupKey = LanguageManager.GetString("Group_MonthFormat", year, month);
            var tile = new MediaTileViewModel(item, groupKey);

            // 同时维护扁平集合（用于全选/拖拽等操作）
            Tiles.Add(tile);

            // 插入到两级分组结构中
            var yearGroup = GetOrCreateYearGroup(year);
            var monthGroup = GetOrCreateMonthGroup(yearGroup, year, month);
            InsertTileIntoMonthGroup(monthGroup, tile);

            // 刷新年份显示标签（总数变化）
            yearGroup.RefreshDisplayLabel();
        }
    }

    /// <summary>获取或创建年份分组（按年份从新到旧排序）。</summary>
    private YearGroupViewModel GetOrCreateYearGroup(int year)
    {
        foreach (var yg in YearGroups)
        {
            if (yg.Year == year)
                return yg;
        }

        var newYearGroup = new YearGroupViewModel(year);
        // 按年份从新到旧插入
        var insertIdx = 0;
        for (int i = 0; i < YearGroups.Count; i++)
        {
            if (YearGroups[i].Year < year)
            {
                insertIdx = i;
                break;
            }
            insertIdx = i + 1;
        }
        YearGroups.Insert(insertIdx, newYearGroup);
        return newYearGroup;
    }

    /// <summary>获取或创建月份分组（按月份从新到旧排序）。</summary>
    private static MonthGroupViewModel GetOrCreateMonthGroup(YearGroupViewModel yearGroup, int year, int month)
    {
        foreach (var mg in yearGroup.MonthGroups)
        {
            if (mg.Month == month)
                return mg;
        }

        var newMonthGroup = new MonthGroupViewModel(year, month);
        // 按月份从新到旧插入
        var insertIdx = 0;
        for (int i = 0; i < yearGroup.MonthGroups.Count; i++)
        {
            if (yearGroup.MonthGroups[i].Month < month)
            {
                insertIdx = i;
                break;
            }
            insertIdx = i + 1;
        }
        yearGroup.MonthGroups.Insert(insertIdx, newMonthGroup);
        return newMonthGroup;
    }

    /// <summary>将 tile 插入到月份分组中，保持按时间从新到旧排序。</summary>
    private static void InsertTileIntoMonthGroup(MonthGroupViewModel monthGroup, MediaTileViewModel tile)
    {
        var newTime = tile.Item.SortTimeUtc;
        for (int i = 0; i < monthGroup.Items.Count; i++)
        {
            if (monthGroup.Items[i].Item.SortTimeUtc < newTime)
            {
                monthGroup.Items.Insert(i, tile);
                return;
            }
        }
        monthGroup.Items.Add(tile);
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

    /// <summary>
    /// 构建拖拽记录：将当前拖拽项与已勾选项合并（去重）。
    /// 如果当前项未勾选，也会被包含在内。
    /// </summary>
    public IReadOnlyList<MediaDragRecord> BuildDragRecordsForDrag(MediaItem draggedItem)
    {
        var seen = new HashSet<string>();
        var result = new List<MediaDragRecord>();

        // 先加入当前拖拽项
        var dragRecord = ToRecord(draggedItem);
        var dragKey = dragRecord.Kind == "fs" ? dragRecord.FsPath : dragRecord.MtpPath;
        if (dragKey != null)
            seen.Add(dragKey);
        result.Add(dragRecord);

        // 再加入已勾选项（跳过重复）
        foreach (var tile in Tiles)
        {
            if (!tile.IsSelected) continue;
            var rec = ToRecord(tile.Item);
            var key = rec.Kind == "fs" ? rec.FsPath : rec.MtpPath;
            if (key != null && !seen.Add(key)) continue;
            result.Add(rec);
        }

        return result;
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
    /// 为MTP设备同步下载文件到临时目录以支持Shell拖拽。
    /// 必须同步执行，因为 DoDragDrop 要求在鼠标按下状态下调用。
    /// </summary>
    public IReadOnlyList<string> StageFilesForShellDragSync(IReadOnlyList<MediaDragRecord> records)
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
                using (var fs = File.Create(dest))
                {
                    dev.DownloadFile(r.MtpPath!, fs);
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
    /// 为拖拽操作构建虚拟文件描述符列表。
    /// 文件系统记录直接返回真实路径流；MTP 记录使用 lambda 在 Shell 请求时按需下载。
    /// 这样可以在不预先下载文件的前提下立即启动 OLE 拖放，避免大文件下载导致的 UI 卡顿。
    /// </summary>
    public IReadOnlyList<VirtualFileDescriptor> BuildVirtualFileDescriptorsForDrag(IReadOnlyList<MediaDragRecord> records)
    {
        var list = new List<VirtualFileDescriptor>();
        var localDevice = _mtpDevice; // 捕获当前设备引用
        var localPnpId = _descriptor.MtpDeviceId;

        foreach (var r in records)
        {
            if (r.Kind == "fs" && !string.IsNullOrEmpty(r.FsPath))
            {
                var fsPath = r.FsPath!;
                if (!File.Exists(fsPath))
                    continue;

                long size = -1;
                DateTime? lwt = null;
                try
                {
                    var fi = new FileInfo(fsPath);
                    size = fi.Length;
                    lwt = fi.LastWriteTimeUtc;
                }
                catch { /* 忽略 */ }

                list.Add(new VirtualFileDescriptor
                {
                    Name = string.IsNullOrWhiteSpace(r.DisplayName) ? Path.GetFileName(fsPath) : r.DisplayName,
                    Length = size,
                    LastWriteTimeUtc = lwt,
                    StreamContents = stream =>
                    {
                        try
                        {
                            using var src = File.OpenRead(fsPath);
                            src.CopyTo(stream);
                        }
                        catch { /* 忽略单文件失败 */ }
                    },
                });
                continue;
            }

            if (r.Kind != "mtp" || string.IsNullOrEmpty(r.MtpPath))
                continue;

            string mtpPath = r.MtpPath!;
            string? pnpId = r.PnpId;
            string displayName = string.IsNullOrWhiteSpace(r.DisplayName)
                ? Path.GetFileName(mtpPath.TrimEnd('\\'))
                : r.DisplayName;

            // 尝试预获取大小和时间（用于 Shell 进度显示）
            long mtpSize = -1;
            DateTime? mtpTime = null;
            if (localDevice != null && localDevice.IsConnected &&
                string.Equals(pnpId, localPnpId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var info = localDevice.GetFileInfo(mtpPath);
                    if (info != null)
                    {
                        try { mtpSize = (long)info.Length; } catch { /* 部分驱动不支持 */ }
                        if (info.LastWriteTime.HasValue && info.LastWriteTime.Value > DateTime.MinValue)
                            mtpTime = info.LastWriteTime.Value.ToUniversalTime();
                        else if (info.CreationTime.HasValue && info.CreationTime.Value > DateTime.MinValue)
                            mtpTime = info.CreationTime.Value.ToUniversalTime();
                    }

                }
                catch { /* 忽略 */ }
            }

            list.Add(new VirtualFileDescriptor
            {
                Name = displayName,
                Length = mtpSize,
                LastWriteTimeUtc = mtpTime,
                StreamContents = stream =>
                {
                    // 此回调由 OLE Shell 在 Drop 后调用（通常在 STA 线程，可能是后台线程）
                    // 直接调用 MediaDevice 同步下载，结果写入 stream
                    try
                    {
                        MediaDevice? dev = localDevice;
                        bool needDisconnect = false;
                        if (dev == null || !dev.IsConnected ||
                            !string.Equals(pnpId, localPnpId, StringComparison.OrdinalIgnoreCase))
                        {
                            dev = MtpDeviceLister.TryConnectByName(pnpId ?? "");
                            needDisconnect = dev != null;
                        }
                        if (dev == null)
                            return;

                        try
                        {
                            dev.DownloadFile(mtpPath, stream);
                        }
                        finally
                        {
                            if (needDisconnect)
                            {
                                try { dev.Disconnect(); } catch { /* 忽略 */ }
                            }
                        }
                    }
                    catch
                    {
                        // 忽略下载失败
                    }
                },
            });
        }

        return list;
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
