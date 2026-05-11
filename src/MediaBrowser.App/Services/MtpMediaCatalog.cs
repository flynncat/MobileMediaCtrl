using System.IO;
using MediaBrowser.Core.MediaFormats;
using MediaBrowser.Core.Models;
using MediaDevices;


namespace MediaBrowser.App.Services;

public static class MtpMediaCatalog
{
    public static Task<IReadOnlyList<MediaItem>> EnumerateAsync(
        MediaDevice device,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnumerateCore(device, cancellationToken), cancellationToken);

    private static IReadOnlyList<MediaItem> EnumerateCore(MediaDevice device, CancellationToken cancellationToken)
    {
        var list = new List<MediaItem>();
        if (!device.IsConnected)
            return list;

        var pnp = device.PnPDeviceID ?? "";

        foreach (var path in device.EnumerateFiles(@"\", "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MediaExtensionLists.IsMediaFile(path))
                continue;

            try
            {
                var fi = device.GetFileInfo(path);
                var folder = fi.Directory?.FullName ?? @"\";
                var sortUtc = SafeLastWriteUtc(fi);

                list.Add(new MediaItem
                {
                    Id = $"mtp:{pnp}|{path}",
                    SourceKind = MediaSourceKind.Mtp,
                    DisplayName = Path.GetFileName(path.TrimEnd('\\')),
                    ContainingFolderPath = folder,
                    SortTimeUtc = sortUtc,
                    IsVideo = MediaExtensionLists.IsVideo(path),
                    MtpDeviceId = pnp,
                    MtpObjectId = path,
                });
            }
            catch
            {
                // 单文件失败时跳过
            }
        }

        return list;
    }

    private static DateTime SafeLastWriteUtc(MediaFileInfo fi)
    {
        try
        {
            return fi.LastWriteTime?.ToUniversalTime() ?? DateTime.UtcNow;

        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}
