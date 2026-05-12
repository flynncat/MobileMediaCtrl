using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaBrowser.App.Services;
using MediaBrowser.App.ViewModels;
using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;
using MediaDevices;


namespace MediaBrowser.App.Views;

public partial class MediaWindow : Window
{
    private System.Windows.Point _dragStart;
    private bool _dragPrepared;
    private readonly MediaWindowViewModel _vm;
    /// <summary>标识本窗口实例，用于检测拖拽到自身窗口的误操作。</summary>
    internal readonly string InstanceId = Guid.NewGuid().ToString("N");

    public MediaWindow(DeviceSessionDescriptor descriptor)
    {
        InitializeComponent();
        _vm = new MediaWindowViewModel(descriptor);
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
        Closed += (_, _) =>
        {
            if (descriptor.Kind == DeviceKind.MtpDevice && !string.IsNullOrEmpty(descriptor.MtpDeviceId))
                ApplicationSession.Coordinator.NotifyMtpSessionClosed(descriptor.MtpDeviceId);
            _vm.Dispose();
        };
    }


    // ── 缩略图懒加载 ──

    private bool _lazyLoadScheduled;

    /// <summary>
    /// ScrollViewer 滚动事件处理：触发缩略图懒加载和 Sticky Header 更新。
    /// </summary>
    private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateStickyHeader();

        if (!_lazyLoadScheduled)
        {
            _lazyLoadScheduled = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                _lazyLoadScheduled = false;
                LoadVisibleThumbnails();
            });
        }
    }

    // ── Sticky Header ──

    /// <summary>
    /// 更新 Sticky Header 覆盖层，显示当前滚动位置对应的年份和月份。
    /// </summary>
    private void UpdateStickyHeader()
    {
        if (MainScrollViewer.VerticalOffset <= 0)
        {
            StickyHeaderBorder.Visibility = Visibility.Collapsed;
            return;
        }

        // 遍历年份分组，找到当前滚动位置对应的年份和月份
        string? currentYear = null;
        string? currentMonth = null;

        foreach (var yearGroup in _vm.YearGroups)
        {
            // 找到年份对应的容器
            var yearContainer = YearItemsControl.ItemContainerGenerator.ContainerFromItem(yearGroup) as FrameworkElement;
            if (yearContainer == null) continue;

            try
            {
                var transform = yearContainer.TransformToAncestor(MainScrollViewer);
                var pos = transform.Transform(new System.Windows.Point(0, 0));

                // 如果年份容器的底部还在视口上方，跳过
                if (pos.Y + yearContainer.ActualHeight < 0)
                    continue;

                // 如果年份容器的顶部在视口下方，停止
                if (pos.Y > MainScrollViewer.ViewportHeight)
                    break;

                // 当前年份标题已滚出顶部或正好在顶部
                if (pos.Y <= 0)
                {
                    currentYear = yearGroup.DisplayLabel;

                    // 查找当前月份
                    if (yearGroup.IsExpanded)
                    {
                        foreach (var monthGroup in yearGroup.MonthGroups)
                        {
                            // 通过 VisualTree 查找月份容器
                            var monthLabel = FindMonthHeaderInVisualTree(yearContainer, monthGroup);
                            if (monthLabel != null)
                            {
                                try
                                {
                                    var monthTransform = monthLabel.TransformToAncestor(MainScrollViewer);
                                    var monthPos = monthTransform.Transform(new System.Windows.Point(0, 0));
                                    if (monthPos.Y <= 0)
                                    {
                                        currentMonth = monthGroup.DisplayLabel;
                                    }
                                }
                                catch { /* TransformToAncestor 可能失败 */ }
                            }
                        }
                    }
                }
                else if (currentYear == null)
                {
                    // 第一个可见的年份还没滚出顶部，不需要 Sticky Header
                    break;
                }
            }
            catch { /* TransformToAncestor 可能失败 */ }
        }

        if (currentYear != null)
        {
            StickyYearText.Text = currentYear;
            StickyMonthText.Text = currentMonth ?? "";
            StickyHeaderBorder.Visibility = Visibility.Visible;
        }
        else
        {
            StickyHeaderBorder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 在 VisualTree 中查找月份标题对应的 Border 元素。
    /// </summary>
    private static FrameworkElement? FindMonthHeaderInVisualTree(DependencyObject parent, MonthGroupViewModel monthGroup)
    {
        // 遍历 VisualTree 查找 Tag="MonthHeader" 且 DataContext 匹配的 Border
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(parent);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is FrameworkElement fe && fe.Tag as string == "MonthHeader" && fe.DataContext == monthGroup)
                return fe;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }

        return null;
    }

    // ── 年份/月份标题点击事件 ──

    private void YearHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is YearGroupViewModel yearGroup)
        {
            yearGroup.IsExpanded = !yearGroup.IsExpanded;
            e.Handled = true;
        }
    }

    private void MonthHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonthGroupViewModel monthGroup)
        {
            monthGroup.IsExpanded = !monthGroup.IsExpanded;
            e.Handled = true;
        }
    }

    // ── 缩略图懒加载 ──

    /// <summary>
    /// 扫描当前可视区域内的 tile，为尚未加载缩略图的 tile 启动异步加载。
    /// </summary>
    private void LoadVisibleThumbnails()
    {
        var viewportHeight = MainScrollViewer.ViewportHeight;

        // 遍历所有年份分组
        foreach (var yearGroup in _vm.YearGroups)
        {
            if (!yearGroup.IsExpanded) continue;

            foreach (var monthGroup in yearGroup.MonthGroups)
            {
                if (!monthGroup.IsExpanded) continue;

                foreach (var tile in monthGroup.Items)
                {
                    // 查找 tile 对应的可视元素
                    var tileElement = FindTileElement(tile);
                    if (tileElement == null) continue;

                    try
                    {
                        var transform = tileElement.TransformToAncestor(MainScrollViewer);
                        var pos = transform.Transform(new System.Windows.Point(0, 0));

                        if (pos.Y + tileElement.ActualHeight < -200 || pos.Y > viewportHeight + 200)
                        {
                            // 不在可视区域（含缓冲区），取消加载
                            tile.CancelThumbnailLoading();
                            continue;
                        }

                        // 在可视区域内，启动缩略图加载
                        if (tile.Thumbnail == null)
                        {
                            _ = LoadThumbnailForTileAsync(tile);
                        }
                    }
                    catch
                    {
                        // TransformToAncestor 可能失败（元素未在可视树中）
                    }
                }
            }
        }
    }

    /// <summary>
    /// 在 VisualTree 中查找 tile 对应的 UI 元素。
    /// </summary>
    private FrameworkElement? FindTileElement(MediaTileViewModel tile)
    {
        // 使用广度优先搜索在 YearItemsControl 中查找 DataContext 匹配的 Border
        return FindElementByDataContext(YearItemsControl, tile);
    }

    private static FrameworkElement? FindElementByDataContext(DependencyObject parent, object dataContext)
    {
        // 优化：只搜索到 Border 层级（媒体卡片的根元素）
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
            {
                if (fe.DataContext == dataContext && fe is System.Windows.Controls.Border)
                    return fe;
            }
            var result = FindElementByDataContext(child, dataContext);
            if (result != null)
                return result;
        }
        return null;
    }


    private async Task LoadThumbnailForTileAsync(MediaTileViewModel tile)
    {
        var cts = tile.GetOrCreateCts();
        try
        {
            await _vm.ThumbGate.WaitAsync(cts.Token).ConfigureAwait(true);
            try
            {
                var bmp = await _vm.ThumbLoader.LoadAsync(tile.Item, _vm.MtpDevice, cancellationToken: cts.Token)
                    .ConfigureAwait(true);
                if (bmp != null && !cts.Token.IsCancellationRequested)
                    tile.Thumbnail = bmp;
            }
            finally
            {
                _vm.ThumbGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 加载被取消，正常情况
        }
    }

    // ── 拖放处理 ──

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        // 检测是否从本窗口发起的拖拽
        bool isFromSelf = InternalDragFormats.ActiveDragSourceWindowId == InstanceId;

        if (isFromSelf)
        {
            // 拖回自身窗口时显示禁止符号
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 接受从其他 MediaWindow 或外部应用拖入的文件
        if (InternalDragFormats.ActiveDragMediaItemsJson != null ||
            e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
    }


    private async void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)

    {
        // 拖拽到自身窗口时忽略（误操作）
        if (InternalDragFormats.ActiveDragSourceWindowId == InstanceId)
        {
            e.Handled = true;
            return;
        }

        // 从其他 MediaWindow 拖入的内部数据
        if (InternalDragFormats.ActiveDragMediaItemsJson is { } json && !string.IsNullOrWhiteSpace(json))
        {
            await _vm.HandleInternalDropAsync(json).ConfigureAwait(true);
            e.Handled = true;
            return;
        }


        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            var items = paths
                .Where(File.Exists)
                .Select(p => new MediaItem
                {
                    Id = p,
                    SourceKind = MediaSourceKind.FileSystem,
                    FileSystemPath = p,
                    DisplayName = Path.GetFileName(p),
                    SortTimeUtc = File.GetLastWriteTimeUtc(p),
                })
                .ToList();

            await PortableTransferService.CopyToDirectoryAsync(
                    items,
                    _vm.CurrentDropTargetPath,
                    mtpDevice: null,
                    new CopyOptions { CollisionPolicy = NameCollisionPolicy.AutoRename })
                .ConfigureAwait(true);
            System.Windows.MessageBox.Show(
                LanguageManager.GetString("VM_ExternalDropDone"),
                LanguageManager.GetString("VM_Done"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
        }
    }

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsUnderCheckBox(e.OriginalSource as DependencyObject))
            return;

        _dragPrepared = true;
        _dragStart = e.GetPosition(null);
        // 新方案：MTP 设备使用 VirtualFileDataObject 按需下载，无需在按下时预启动后台任务。
    }

    private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragPrepared = false;
    }





    private void Tile_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragPrepared || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (IsUnderCheckBox(e.OriginalSource as DependencyObject))
            return;

        var pos = e.GetPosition(null);
        var diff = pos - _dragStart;
        if (Math.Abs(diff.X) < 6 && Math.Abs(diff.Y) < 6)
            return;

        if (sender is not FrameworkElement fe || fe.DataContext is not MediaTileViewModel tile)
            return;

        _dragPrepared = false;

        // 构建拖拽记录：当前拖拽项 + 已勾选项（去重合并）
        var records = _vm.BuildDragRecordsForDrag(tile.Item);
        if (records.Count == 0)
            return;

        // 通过进程内静态变量传递内部拖放信息（兼容窗口间内部拖放）
        InternalDragFormats.ActiveDragSourceWindowId = InstanceId;
        InternalDragFormats.ActiveDragMediaItemsJson = JsonSerializer.Serialize(records, MediaDragJson.Options);

        try
        {
            if (_vm.IsFileSystemDevice)
            {
                // 文件系统设备：直接使用真实文件路径 + FileDrop，零延迟、最佳兼容性
                var fsPaths = _vm.BuildShellDragPathsForFileSystem(records);
                if (fsPaths.Count == 0)
                    return;

                var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, fsPaths.ToArray());
                DragDrop.DoDragDrop(fe, data, System.Windows.DragDropEffects.Copy);
            }
            else
            {
                // MTP 设备：使用 VirtualFileDataObject。
                // 立即启动 OLE 拖放，文件内容在 Shell 调用 IStream::Read 时按需下载。
                // 优点：无需预下载，无 UI 卡顿，用户可以拖拽任意大小文件。
                var descriptors = _vm.BuildVirtualFileDescriptorsForDrag(records);
                if (descriptors.Count == 0)
                    return;

                var virtualData = new VirtualFileDataObject(descriptors, System.Windows.DragDropEffects.Copy);
                // 关键：显式转型为 ComTypes.IDataObject，调用 DataObject(ComTypes.IDataObject) 构造函数。
                // 该构造函数会直接把所有调用桥接到底层 IDataObject，从而暴露 FileGroupDescriptorW 等格式。
                System.Runtime.InteropServices.ComTypes.IDataObject comData = virtualData;
                var wrapper = new System.Windows.DataObject(comData);
                DragDrop.DoDragDrop(fe, wrapper, System.Windows.DragDropEffects.Copy);

            }
        }
        catch
        {
            // 忽略拖放异常
        }
        finally
        {
            InternalDragFormats.ClearDragState();
        }
    }


    /// <summary>双击缩略图卡片时打开预览窗口。</summary>

    private async void Tile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        if (IsUnderCheckBox(e.OriginalSource as DependencyObject))
            return;

        if (sender is not FrameworkElement fe || fe.DataContext is not MediaTileViewModel tile)
            return;

        // 取消拖拽准备，防止双击触发拖拽
        _dragPrepared = false;

        var preview = new PreviewWindow();
        preview.Owner = this;
        preview.Show();
        await preview.LoadMediaAsync(tile.Item, _vm.MtpDevice).ConfigureAwait(true);

        e.Handled = true;
    }

    private static bool IsUnderCheckBox(DependencyObject? src)
    {
        while (src is not null)
        {
            if (src is System.Windows.Controls.CheckBox)
                return true;
            src = VisualTreeHelper.GetParent(src);
        }

        return false;
    }
}

