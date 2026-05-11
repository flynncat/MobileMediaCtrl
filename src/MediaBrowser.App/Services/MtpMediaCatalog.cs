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

        // 使用 FriendlyName 作为设备标识（与 DeviceSessionDescriptor.MtpDeviceId 一致）
        var deviceName = device.FriendlyName ?? "";

        // MTP 设备的根目录 "\" 下是存储卷（如 "Internal shared storage"），
        // 不能直接对 "\" 调用 EnumerateFiles，否则会抛出 COMException 0x80042009。
        // 需要先获取存储根目录列表，再逐个枚举。
        IEnumerable<string> storageRoots;
        try
        {
            storageRoots = device.EnumerateDirectories(@"\");
        }
        catch
        {
            return list;
        }

        foreach (var storageRoot in storageRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateStorage(device, storageRoot, deviceName, list, cancellationToken);
        }

        return list;
    }

    private static void EnumerateStorage(
        MediaDevice device,
        string storagePath,
        string deviceName,
        List<MediaItem> list,
        CancellationToken cancellationToken)
    {
        // 使用手动递归方式逐目录枚举，避免惰性迭代器在 foreach 中抛出 COMException。
        var dirStack = new Stack<string>();
        dirStack.Push(storagePath);

        while (dirStack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDir = dirStack.Pop();

            // 枚举当前目录下的子目录
            try
            {
                foreach (var subDir in device.EnumerateDirectories(currentDir))
                {
                    dirStack.Push(subDir);
                }
            }
            catch
            {
                // 无法访问的目录，跳过
            }

            // 枚举当前目录下的文件（仅当前层，不递归）
            IEnumerable<string> files;
            try
            {
                files = device.EnumerateFiles(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (var path in SafeEnumerate(files))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MediaExtensionLists.IsMediaFile(path))
                    continue;

                try
                {
                    var fi = device.GetFileInfo(path);
                    var folder = fi.Directory?.FullName ?? currentDir;
                    var sortUtc = SafeLastWriteUtc(fi);

                    list.Add(new MediaItem
                    {
                        Id = $"mtp:{deviceName}|{path}",
                        SourceKind = MediaSourceKind.Mtp,
                        DisplayName = Path.GetFileName(path.TrimEnd('\\')),
                        ContainingFolderPath = folder,
                        SortTimeUtc = sortUtc,
                        IsVideo = MediaExtensionLists.IsVideo(path),
                        MtpDeviceId = deviceName,
                        MtpObjectId = path,
                    });
                }
                catch
                {
                    // 单文件失败时跳过
                }
            }
        }
    }

    /// <summary>
    /// 安全迭代惰性枚举器，遇到异常时终止迭代而非抛出。
    /// </summary>
    private static IEnumerable<string> SafeEnumerate(IEnumerable<string> source)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            string? current;
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
                current = enumerator.Current;
            }
            catch
            {
                yield break;
            }
            yield return current;
        }
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
