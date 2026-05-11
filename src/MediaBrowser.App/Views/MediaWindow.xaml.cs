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

        // 监听滚动事件，实现缩略图懒加载
        MediaListBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    // ── 缩略图懒加载 ──

    private bool _lazyLoadScheduled;

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
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

    /// <summary>
    /// 扫描当前可视区域内的 tile，为尚未加载缩略图的 tile 启动异步加载。
    /// </summary>
    private void LoadVisibleThumbnails()
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(MediaListBox);
        if (scrollViewer == null) return;

        var viewportTop = scrollViewer.VerticalOffset;
        var viewportBottom = viewportTop + scrollViewer.ViewportHeight;

        // 遍历所有可见的 ListBoxItem
        for (int i = 0; i < MediaListBox.Items.Count; i++)
        {
            if (MediaListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            // 检查容器是否在可视区域内
            var transform = container.TransformToAncestor(scrollViewer);
            var topLeft = transform.Transform(new System.Windows.Point(0, 0));
            var bottomRight = transform.Transform(new System.Windows.Point(container.ActualWidth, container.ActualHeight));

            if (bottomRight.Y < 0 || topLeft.Y > scrollViewer.ViewportHeight)
            {
                // 不在可视区域，取消加载
                if (container.DataContext is MediaTileViewModel offTile)
                    offTile.CancelThumbnailLoading();
                continue;
            }

            // 在可视区域内，启动缩略图加载
            if (container.DataContext is MediaTileViewModel tile && tile.Thumbnail == null)
            {
                _ = LoadThumbnailForTileAsync(tile);
            }
        }
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

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
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

    private async void Tile_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
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

        if (_vm.IsFileSystemDevice)
        {
            // 文件系统设备：直接使用文件路径，零延迟
            var shellPaths = _vm.BuildShellDragPathsForFileSystem(records);
            if (shellPaths.Count > 0)
                data.SetData(System.Windows.DataFormats.FileDrop, shellPaths.ToArray(), autoConvert: true);
        }
        else
        {
            // MTP设备：需要先下载到临时目录
            // 使用状态提示告知用户正在准备文件
            var originalStatus = _vm.StatusText;
            _vm.StatusText = LanguageManager.GetString("VM_PreparingDrag");
            try
            {
                var shellPaths = await _vm.StageFilesForShellDragAsync(records).ConfigureAwait(true);
                if (shellPaths.Count > 0)
                    data.SetData(System.Windows.DataFormats.FileDrop, shellPaths.ToArray(), autoConvert: true);
            }
            finally
            {
                _vm.StatusText = originalStatus;
            }
        }

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

