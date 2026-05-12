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
        // 检测是否拖拽到自身窗口（误操作）
        if (e.Data.GetDataPresent(InternalDragFormats.SourceWindowId))
        {
            var sourceId = e.Data.GetData(InternalDragFormats.SourceWindowId) as string;
            if (sourceId == InstanceId)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }
        }

        if (e.Data.GetDataPresent(InternalDragFormats.MediaItems) ||
            e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        // 拖拽到自身窗口时忽略（误操作）
        if (e.Data.GetDataPresent(InternalDragFormats.SourceWindowId))
        {
            var sourceId = e.Data.GetData(InternalDragFormats.SourceWindowId) as string;
            if (sourceId == InstanceId)
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Data.GetDataPresent(InternalDragFormats.MediaItems))
        {
            var json = e.Data.GetData(InternalDragFormats.MediaItems) as string;
            if (!string.IsNullOrWhiteSpace(json))
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
    }

    private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _dragPrepared = false;

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

        var records = _vm.BuildDragRecordsForSelection();
        if (records.Count == 0)
        {
            records = _vm.BuildDragRecords(new[] { tile.Item });
        }

        var data = new System.Windows.DataObject();

        // 附带源窗口标识，用于检测拖拽到自身窗口的误操作
        data.SetData(InternalDragFormats.SourceWindowId, InstanceId);
        data.SetData(InternalDragFormats.MediaItems, JsonSerializer.Serialize(records, MediaDragJson.Options));

        // 同步准备 Shell 拖拽路径（必须同步，否则 DoDragDrop 无法在鼠标按下状态启动）
        IReadOnlyList<string> shellPaths;
        if (_vm.IsFileSystemDevice)
        {
            // 文件系统设备：直接使用文件路径，零延迟
            shellPaths = _vm.BuildShellDragPathsForFileSystem(records);
        }
        else
        {
            // MTP设备：同步下载到临时目录（显示等待光标）
            var prevCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            try
            {
                shellPaths = _vm.StageFilesForShellDragSync(records);
            }
            finally
            {
                Mouse.OverrideCursor = prevCursor;
            }
        }

        if (shellPaths.Count > 0)
            data.SetData(System.Windows.DataFormats.FileDrop, shellPaths.ToArray(), autoConvert: true);

        try
        {
            DragDrop.DoDragDrop(fe, data, System.Windows.DragDropEffects.Copy);
        }
        catch
        {
            // 忽略拖放被取消
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

