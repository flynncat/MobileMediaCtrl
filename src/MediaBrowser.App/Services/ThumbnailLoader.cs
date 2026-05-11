using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using MediaBrowser.Core.Models;
using MediaDevices;

namespace MediaBrowser.App.Services;

public sealed class ThumbnailLoader
{
    private readonly string _cacheRoot;

    public ThumbnailLoader()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaBrowser",
            "thumbs");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<BitmapSource?> LoadAsync(MediaItem item, MediaDevice? mtpDevice, int maxEdge = 200,
        CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(item);
        if (File.Exists(cachePath))
        {
            return await LoadBitmapFromFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // ── 文件系统来源 ──
            if (item.SourceKind == MediaSourceKind.FileSystem && !string.IsNullOrEmpty(item.FileSystemPath))
            {
                await Task.Run(() => BuildThumbnailToFile(item.FileSystemPath!, cachePath, maxEdge, item.IsVideo),
                    cancellationToken).ConfigureAwait(false);

                if (File.Exists(cachePath))
                    return await LoadBitmapFromFileAsync(cachePath, cancellationToken).ConfigureAwait(false);

                return null;
            }

            // ── MTP 来源 ──
            if (item.SourceKind == MediaSourceKind.Mtp && mtpDevice is { IsConnected: true } &&
                !string.IsNullOrEmpty(item.MtpObjectId))
            {
                // 优先尝试 DownloadThumbnail（速度快），失败则回退下载原文件生成缩略图
                var thumbData = await Task.Run(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        mtpDevice.DownloadThumbnail(item.MtpObjectId!, ms);
                        if (ms.Length > 0)
                            return ms.ToArray();
                    }
                    catch
                    {
                        // DownloadThumbnail 不可用（0x8007138E 等），忽略
                    }
                    return null;
                }, cancellationToken).ConfigureAwait(false);

                if (thumbData is { Length: > 0 })
                {
                    await using var fs = File.Create(cachePath);
                    await fs.WriteAsync(thumbData, cancellationToken).ConfigureAwait(false);
                    return await LoadBitmapFromFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
                }

                // 回退：下载原始文件到临时目录，再生成缩略图（图片和视频均支持）
                var tempPath = await DownloadMtpToTempAsync(item, mtpDevice, cancellationToken).ConfigureAwait(false);
                if (tempPath != null)
                {
                    try
                    {
                        await Task.Run(() => BuildThumbnailToFile(tempPath, cachePath, maxEdge, item.IsVideo),
                            cancellationToken).ConfigureAwait(false);

                        if (File.Exists(cachePath))
                            return await LoadBitmapFromFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        // 视频临时文件可能较大，用完即删
                        TryDeleteFile(tempPath);
                    }
                }

                return null;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    // ── 缩略图生成（图片 + 视频） ──

    private static void BuildThumbnailToFile(string sourcePath, string destPath, int maxEdge, bool isVideo)
    {
        if (isVideo)
        {
            BuildVideoThumbnailViaShell(sourcePath, destPath, maxEdge);
        }
        else
        {
            BuildImageThumbnail(sourcePath, destPath, maxEdge);
        }
    }

    private static void BuildImageThumbnail(string sourcePath, string destPath, int maxEdge)
    {
        using var img = Image.FromFile(sourcePath, useEmbeddedColorManagement: true);
        var w = img.Width;
        var h = img.Height;
        var scale = Math.Min(1.0, Math.Min((double)maxEdge / w, (double)maxEdge / h));
        var tw = Math.Max(1, (int)(w * scale));
        var th = Math.Max(1, (int)(h * scale));
        using var bmp = new Bitmap(tw, th);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, new Rectangle(0, 0, tw, th));
        }
        bmp.Save(destPath, ImageFormat.Jpeg);
    }

    /// <summary>
    /// 使用 Windows Shell API (IShellItemImageFactory) 提取视频缩略图。
    /// Windows 资源管理器使用同一接口，支持所有已注册解码器的视频格式。
    /// </summary>
    private static void BuildVideoThumbnailViaShell(string videoPath, string destPath, int maxEdge)
    {
        var hr = SHCreateItemFromParsingName(videoPath, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        if (hr != 0 || factory == null)
            return;

        try
        {
            var size = new NativeSize(maxEdge, maxEdge);
            // SIIGBF_THUMBNAILONLY = 0x04 — 仅返回缩略图，不回退到图标
            // SIIGBF_BIGGERSIZEOK = 0x08 — 允许返回比请求尺寸更大的缩略图
            hr = factory.GetImage(size, 0x04 | 0x08, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                // 回退：不加 THUMBNAILONLY 标志再试一次
                hr = factory.GetImage(size, 0x08, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return;
            }

            try
            {
                using var bmp = Image.FromHbitmap(hBitmap);
                bmp.Save(destPath, ImageFormat.Jpeg);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    // ── MTP 文件下载到临时目录 ──

    private static async Task<string?> DownloadMtpToTempAsync(MediaItem item, MediaDevice mtpDevice,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "MediaBrowserThumbs");
                Directory.CreateDirectory(tempDir);
                var tempPath = Path.Combine(tempDir, item.DisplayName);

                using var fs = File.Create(tempPath);
                mtpDevice.DownloadFile(item.MtpObjectId!, fs);
                return fs.Length > 0 ? tempPath : null;
            }
            catch
            {
                return null;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    // ── 辅助方法 ──

    private string GetCachePath(MediaItem item)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(item.Id)))[..16];
        var safe = $"{hash}_{Sanitize(item.DisplayName)}.jpg";
        return Path.Combine(_cacheRoot, safe);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "thumb" : name[..Math.Min(name.Length, 80)];
    }

    private static async Task<BitmapSource?> LoadBitmapFromFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(path);
        var ms = new MemoryStream();
        await fs.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* 忽略 */ }
    }

    // ══════════════════════════════════════════════════════════════
    //  Windows Shell API — IShellItemImageFactory P/Invoke
    // ══════════════════════════════════════════════════════════════

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }
}
