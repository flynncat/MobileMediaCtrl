using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
            if (item.SourceKind == MediaSourceKind.FileSystem && !string.IsNullOrEmpty(item.FileSystemPath))
            {
                await Task.Run(() => BuildThumbnailToFile(item.FileSystemPath!, cachePath, maxEdge), cancellationToken)
                    .ConfigureAwait(false);
                return await LoadBitmapFromFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
            }

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

                // 回退：下载原始文件再生成缩略图（仅图片，视频跳过）
                if (!item.IsVideo)
                {
                    var fileData = await Task.Run(() =>
                    {
                        try
                        {
                            using var ms = new MemoryStream();
                            mtpDevice.DownloadFile(item.MtpObjectId!, ms);
                            if (ms.Length > 0)
                                return ms.ToArray();
                        }
                        catch
                        {
                            // 下载失败，跳过
                        }
                        return null;
                    }, cancellationToken).ConfigureAwait(false);

                    if (fileData is { Length: > 0 })
                    {
                        await Task.Run(() =>
                        {
                            using var imgStream = new MemoryStream(fileData);
                            using var img = Image.FromStream(imgStream, useEmbeddedColorManagement: true);
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
                            bmp.Save(cachePath, System.Drawing.Imaging.ImageFormat.Jpeg);
                        }, cancellationToken).ConfigureAwait(false);

                        return await LoadBitmapFromFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
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

    private static void BuildThumbnailToFile(string sourcePath, string destPath, int maxEdge)
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

        bmp.Save(destPath, System.Drawing.Imaging.ImageFormat.Jpeg);
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
}
