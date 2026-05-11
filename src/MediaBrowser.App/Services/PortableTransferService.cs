using System.IO;
using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;
using MediaDevices;


namespace MediaBrowser.App.Services;

public static class PortableTransferService
{
    public static async Task<CopyResult> CopyToDirectoryAsync(
        IEnumerable<MediaItem> items,
        string targetDirectory,
        MediaDevice? mtpDevice,
        CopyOptions options,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var success = 0;
        var skipped = 0;
        var failed = 0;

        Directory.CreateDirectory(targetDirectory);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.SourceKind == MediaSourceKind.FileSystem)
            {
                var r = FileSystemCopyService.CopyFileSystemItems(new[] { item }, targetDirectory, options, cancellationToken);
                success += r.SuccessCount;
                skipped += r.SkippedCount;
                failed += r.FailedCount;
                errors.AddRange(r.Errors);
                continue;
            }

            if (item.SourceKind != MediaSourceKind.Mtp || mtpDevice is null ||
                string.IsNullOrEmpty(item.MtpObjectId) || !mtpDevice.IsConnected)
            {
                skipped++;
                continue;
            }

            var name = string.IsNullOrWhiteSpace(item.DisplayName)
                ? Path.GetFileName(item.MtpObjectId.TrimEnd('\\'))
                : item.DisplayName;
            var dest = Path.Combine(targetDirectory, name);
            dest = ResolveDestinationPath(dest, options.CollisionPolicy, ref skipped, ref failed, errors);
            if (dest is null)
                continue;

            try
            {
                await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                               bufferSize: 1 << 16, useAsync: true))
                {
                    await Task.Run(() => mtpDevice.DownloadFile(item.MtpObjectId!, fs), cancellationToken)
                        .ConfigureAwait(false);
                }

                success++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{name}: {ex.Message}");
            }
        }

        return new CopyResult
        {
            SuccessCount = success,
            SkippedCount = skipped,
            FailedCount = failed,
            Errors = errors,
        };
    }

    private static string? ResolveDestinationPath(
        string dest,
        NameCollisionPolicy policy,
        ref int skipped,
        ref int failed,
        List<string> errors)
    {
        if (!File.Exists(dest))
            return dest;

        switch (policy)
        {
            case NameCollisionPolicy.Skip:
                skipped++;
                return null;
            case NameCollisionPolicy.Overwrite:
                return dest;
            case NameCollisionPolicy.AutoRename:
                var dir = Path.GetDirectoryName(dest)!;
                var name = Path.GetFileNameWithoutExtension(dest);
                var ext = Path.GetExtension(dest);
                for (var i = 1; i < 10000; i++)
                {
                    var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                    if (!File.Exists(candidate))
                        return candidate;
                }

                failed++;
                errors.Add($"无法为文件生成唯一名称: {dest}");
                return null;
            default:
                skipped++;
                return null;
        }
    }
}
