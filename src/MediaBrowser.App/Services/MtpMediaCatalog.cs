using System.IO;
using MediaBrowser.Core.MediaFormats;
using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;
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

    private const int BatchSize = 20;

    /// <summary>
    /// 异步枚举设备媒体文件，支持进度回调（每发现一批文件就报告）。
    /// </summary>
    public static Task<IReadOnlyList<MediaItem>> EnumerateAsync(
        MediaDevice device,
        IProgress<MtpScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        EnumerateAsync(device, progress, batchCallback: null, cancellationToken);

    /// <summary>
    /// 异步枚举设备媒体文件，支持增量批次回调。
    /// <paramref name="batchCallback"/> 每发现一批文件（约20个）就推送给调用方，用于增量显示。
    /// </summary>
    public static Task<IReadOnlyList<MediaItem>> EnumerateAsync(
        MediaDevice device,
        IProgress<MtpScanProgress>? progress,
        Action<IReadOnlyList<MediaItem>>? batchCallback,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnumerateCore(device, progress, batchCallback, cancellationToken), cancellationToken);

    private static IReadOnlyList<MediaItem> EnumerateCore(
        MediaDevice device,
        IProgress<MtpScanProgress>? progress,
        Action<IReadOnlyList<MediaItem>>? batchCallback,
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

        progress?.Report(new MtpScanProgress(LanguageManager.GetString("Mtp_FoundVolumes", storageRoots.Count), 0));

        // 批次缓冲区，累积到 BatchSize 个文件后推送
        var pendingBatch = new List<MediaItem>();

        foreach (var storageRoot in storageRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanMediaDirectories(device, storageRoot, deviceName, list, pendingBatch, batchCallback, progress, cancellationToken);
        }

        // 推送剩余不足一批的文件
        if (pendingBatch.Count > 0)
        {
            batchCallback?.Invoke(pendingBatch.ToList());
            pendingBatch.Clear();
        }


        progress?.Report(new MtpScanProgress(LanguageManager.GetString("Mtp_ScanDone", list.Count), list.Count));

        return list;
    }

    private static void ScanMediaDirectories(
        MediaDevice device,
        string storagePath,
        string deviceName,
        List<MediaItem> list,
        List<MediaItem> pendingBatch,
        Action<IReadOnlyList<MediaItem>>? batchCallback,
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
            progress?.Report(new MtpScanProgress(LanguageManager.GetString("Mtp_Scanning", dirName, list.Count), list.Count));

            EnumerateDirectoryRecursive(device, dir, deviceName, list, pendingBatch, batchCallback, progress, cancellationToken, maxDepth: 10);
        }
    }

    private static void EnumerateDirectoryRecursive(
        MediaDevice device,
        string currentDir,
        string deviceName,
        List<MediaItem> list,
        List<MediaItem> pendingBatch,
        Action<IReadOnlyList<MediaItem>>? batchCallback,
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

                // 跳过以 '.' 开头的隐藏文件（macOS 的 ._xxx、.DS_Store 等）
                var fileNameRaw = Path.GetFileName(path.TrimEnd('\\'));
                if (fileNameRaw.StartsWith('.'))
                    continue;

                if (!MediaExtensionLists.IsMediaFile(path))
                    continue;

                var fileName = fileNameRaw;

                var fileTime = FileNameDateParser.TryParse(fileName)
                               ?? TryGetFileTimeUtc(device, path)
                               ?? DateTime.UtcNow;



                var item = new MediaItem
                {
                    Id = $"mtp:{deviceName}|{path}",
                    SourceKind = MediaSourceKind.Mtp,
                    DisplayName = fileName,
                    ContainingFolderPath = currentDir,
                    SortTimeUtc = fileTime,
                    IsVideo = MediaExtensionLists.IsVideo(path),
                    MtpDeviceId = deviceName,
                    MtpObjectId = path,
                };

                list.Add(item);
                pendingBatch.Add(item);

                // 累积到一批后推送并报告进度
                if (pendingBatch.Count >= BatchSize)
                {
                    batchCallback?.Invoke(pendingBatch.ToList());
                    pendingBatch.Clear();
                    progress?.Report(new MtpScanProgress(
                        LanguageManager.GetString("Mtp_FoundFiles", list.Count), list.Count));
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

            EnumerateDirectoryRecursive(device, subDir, deviceName, list, pendingBatch, batchCallback, progress, cancellationToken, maxDepth - 1);

        }
    }

    /// <summary>
    /// 尝试通过 MTP 协议获取文件的修改时间（UTC）。
    /// </summary>
    private static DateTime? TryGetFileTimeUtc(MediaDevice device, string path)
    {
        try
        {
            var info = device.GetFileInfo(path);
            // 优先使用 LastWriteTime，其次 CreationTime
            if (info.LastWriteTime.HasValue && info.LastWriteTime.Value > DateTime.MinValue)
                return info.LastWriteTime.Value.ToUniversalTime();
            if (info.CreationTime.HasValue && info.CreationTime.Value > DateTime.MinValue)
                return info.CreationTime.Value.ToUniversalTime();
        }
        catch
        {
            // GetFileInfo 可能抛出 COM 异常，忽略
        }
        return null;
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
