using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MediaBrowser.App.Services;
using MediaBrowser.App.ViewModels;
using MediaBrowser.Core.Models;


namespace MediaBrowser.App.Views;

public partial class PreviewWindow : Window
{
    private string? _tempFilePath;


    public new string Title { get; set; } = "";

    private bool _isPlaying;

    public PreviewWindow()
    {
        InitializeComponent();
        Title = LanguageManager.GetString("Preview_Title");
        DataContext = this;
    }


    /// <summary>
    /// 加载并预览指定的媒体项。通过 ViewModel 串行访问 MTP 设备，避免与拖拽等并发冲突。
    /// </summary>
    public async Task LoadMediaAsync(MediaItem item, MediaWindowViewModel vm)
    {
        Title = item.DisplayName;
        OnPropertyChanged(nameof(Title));

        try
        {
            string filePath;

            if (item.SourceKind == MediaSourceKind.FileSystem && !string.IsNullOrEmpty(item.FileSystemPath))
            {
                filePath = item.FileSystemPath;
            }
            else if (item.SourceKind == MediaSourceKind.Mtp && !string.IsNullOrEmpty(item.MtpObjectId))
            {
                // 从 MTP 设备下载到临时文件（通过 VM 串行化访问，避免与拖拽并发冲突）
                var tempDir = Path.Combine(Path.GetTempPath(), "MediaBrowserPreview");
                Directory.CreateDirectory(tempDir);
                filePath = Path.Combine(tempDir, item.DisplayName);

                bool ok = false;
                await using (var fs = File.Create(filePath))
                {
                    ok = await Task.Run(() => vm.DownloadMtpFileTo(item.MtpObjectId!, fs)).ConfigureAwait(true);
                }

                if (!ok)
                {
                    LoadingText.Text = LanguageManager.GetString("Preview_CannotPreview");
                    try { File.Delete(filePath); } catch { /* 忽略 */ }
                    return;
                }

                _tempFilePath = filePath;
            }
            else
            {
                LoadingText.Text = LanguageManager.GetString("Preview_CannotPreview");

                return;
            }

            if (item.IsVideo)
            {
                ShowVideo(filePath);
            }
            else
            {
                ShowImage(filePath);
            }
        }
        catch (Exception ex)
        {
            LoadingText.Text = LanguageManager.GetString("Preview_Failed", ex.Message);

        }
    }


    private void ShowImage(string filePath)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(filePath, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        ImageViewer.Source = bmp;
        ImageViewer.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void ShowVideo(string filePath)
    {
        VideoPlayer.Source = new Uri(filePath, UriKind.Absolute);
        VideoPlayer.Visibility = Visibility.Visible;
        VideoControls.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;

        VideoPlayer.Play();
        _isPlaying = true;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            VideoPlayer.Pause();
            _isPlaying = false;
        }
        else
        {
            VideoPlayer.Play();
            _isPlaying = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)

    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Space && VideoPlayer.Visibility == Visibility.Visible)
        {
            PlayPause_Click(sender, e);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        VideoPlayer.Stop();
        VideoPlayer.Source = null;

        // 清理临时文件
        if (_tempFilePath != null)
        {
            try { File.Delete(_tempFilePath); } catch { }
        }

        base.OnClosed(e);
    }

    private void OnPropertyChanged(string propertyName)
    {
        // 简单通知标题更新
    }
}
