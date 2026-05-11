using System.IO;
using MediaBrowser.Core.MediaFormats;
using MediaBrowser.Core.Models;
using MediaDevices;

namespace MediaBrowser.App.Services;

public static class MtpMediaCatalog
{
    // 只扫描这些已知的媒体目录，跳过 Android/data、Android/obb 等大量无关目录
    private static readonly string[] KnownMediaFolders =
    {
        "DCIM", "Pictures", "Movies", "Download", "Downloads",
        "Screenshots", "Camera", "Video", "Photo", "Media",
        "100ANDRO", "WhatsApp", "Telegram", "Snapchat",
    };

    /// <summary>
    /// 异步枚举设备媒体文件，支持进度回调（每发现一批文件就报告）。
    /// </summary>
    public static Task<IReadOnlyList<MediaItem>> EnumerateAsync(
        MediaDevice device,
        IProgress<MtpScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnumerateCore(device, progress, cancellationToken), cancellationToken);

    private static IReadOnlyList<MediaItem> EnumerateCore(
        MediaDevice device,
        IProgress<MtpScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var list = new List<MediaItem>();
        if (!device.IsConnected)
            return list;

        var deviceName = device.FriendlyName ?? "";

        // 获取存储卷列表
        List<string> storageRoots;
        try
        {
            storageRoots = SafeEnumerate(device.EnumerateDirectories(@"\")).ToList();
        }
        catch
        {
            return list;
        }

        if (storageRoots.Count == 0)
            return list;

        progress?.Report(new MtpScanProgress($"发现 {storageRoots.Count} 个存储卷，开始扫描…", 0));

        foreach (var storageRoot in storageRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanMediaDirectories(device, storageRoot, deviceName, list, progress, cancellationToken);
        }

        progress?.Report(new MtpScanProgress($"扫描完成，共 {list.Count} 个媒体文件。", list.Count));
        return list;
    }

    private static void ScanMediaDirectories(
        MediaDevice device,
        string storagePath,
        string deviceName,
        List<MediaItem> list,
        IProgress<MtpScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 获取存储卷下的顶层目录
        List<string> topDirs;
        try
        {
            topDirs = SafeEnumerate(device.EnumerateDirectories(storagePath)).ToList();
        }
        catch
        {
            return;
        }

        // 筛选出已知的媒体目录（不区分大小写）
        var mediaDirs = topDirs
            .Where(d => KnownMediaFolders.Any(known =>
                Path.GetFileName(d.TrimEnd('\\'))
                    .Equals(known, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 如果没有匹配的已知目录，回退扫描所有顶层目录（但不递归太深）
        if (mediaDirs.Count == 0)
            mediaDirs = topDirs;

        foreach (var dir in mediaDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(dir.TrimEnd('\\'));
            progress?.Report(new MtpScanProgress($"正在扫描 {dirName}…（已找到 {list.Count} 个文件）", list.Count));
            EnumerateDirectoryRecursive(device, dir, deviceName, list, progress, cancellationToken, maxDepth: 10);
        }
    }

    private static void EnumerateDirectoryRecursive(
        MediaDevice device,
        string currentDir,
        string deviceName,
        List<MediaItem> list,
        IProgress<MtpScanProgress>? progress,
        CancellationToken cancellationToken,
        int maxDepth)
    {
        if (maxDepth <= 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        // 枚举当前目录下的文件
        try
        {
            foreach (var path in SafeEnumerate(device.EnumerateFiles(currentDir)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MediaExtensionLists.IsMediaFile(path))
                    continue;

                list.Add(new MediaItem
                {
                    Id = $"mtp:{deviceName}|{path}",
                    SourceKind = MediaSourceKind.Mtp,
                    DisplayName = Path.GetFileName(path.TrimEnd('\\')),
                    ContainingFolderPath = currentDir,
                    SortTimeUtc = DateTime.UtcNow, // MTP 获取时间太慢，先用当前时间
                    IsVideo = MediaExtensionLists.IsVideo(path),
                    MtpDeviceId = deviceName,
                    MtpObjectId = path,
                });

                // 每发现 20 个文件报告一次进度
                if (list.Count % 20 == 0)
                {
                    progress?.Report(new MtpScanProgress(
                        $"已找到 {list.Count} 个媒体文件…", list.Count));
                }
            }
        }
        catch
        {
            // 文件枚举失败，跳过
        }

        // 递归子目录
        List<string> subDirs;
        try
        {
            subDirs = SafeEnumerate(device.EnumerateDirectories(currentDir)).ToList();
        }
        catch
        {
            return;
        }

        foreach (var subDir in subDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 跳过已知的无用子目录
            var name = Path.GetFileName(subDir.TrimEnd('\\'));
            if (name.StartsWith(".", StringComparison.Ordinal) ||
                name.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("thumbnails", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("trash", StringComparison.OrdinalIgnoreCase))
                continue;

            EnumerateDirectoryRecursive(device, subDir, deviceName, list, progress, cancellationToken, maxDepth - 1);
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
}

/// <summary>
/// MTP 扫描进度信息
/// </summary>
public record MtpScanProgress(string Message, int FileCount);
