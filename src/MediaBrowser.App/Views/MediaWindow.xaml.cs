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

namespace MediaBrowser.App.Views;

public partial class MediaWindow : Window
{
    private Point _dragStart;
    private bool _dragPrepared;
    private readonly MediaWindowViewModel _vm;

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

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormats.MediaItems) ||
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormats.MediaItems))
        {
            var json = e.Data.GetData(InternalDragFormats.MediaItems) as string;
            if (!string.IsNullOrWhiteSpace(json))
                await _vm.HandleInternalDropAsync(json).ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths)
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
            MessageBox.Show("已将从外部拖入的文件复制到当前路径栏目录。", "完成", MessageBoxButton.OK,
                MessageBoxImage.Information);
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

    private async void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
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

        var data = new DataObject();
        data.SetData(InternalDragFormats.MediaItems, JsonSerializer.Serialize(records, MediaDragJson.Options));

        var paths = await _vm.StageFilesForShellDragAsync(records).ConfigureAwait(true);
        if (paths.Count > 0)
            data.SetData(DataFormats.FileDrop, paths.ToArray(), autoConvert: true);

        try
        {
            DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
        }
        catch
        {
            // 忽略拖放被取消
        }
    }

    private static bool IsUnderCheckBox(DependencyObject? src)
    {
        while (src is not null)
        {
            if (src is CheckBox)
                return true;
            src = VisualTreeHelper.GetParent(src);
        }

        return false;
    }
}
