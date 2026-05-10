using MediaBrowser.Core.Models;

namespace MediaBrowser.Core.Services;

/// <summary>将已具备本地路径的媒体项复制到目标目录。</summary>
public static class FileSystemCopyService
{
    public static CopyResult CopyFileSystemItems(
        IEnumerable<MediaItem> items,
        string targetDirectory,
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
            if (item.SourceKind != MediaSourceKind.FileSystem || string.IsNullOrEmpty(item.FileSystemPath))
            {
                skipped++;
                continue;
            }

            if (!File.Exists(item.FileSystemPath))
            {
                failed++;
                errors.Add($"找不到文件: {item.FileSystemPath}");
                continue;
            }

            var name = Path.GetFileName(item.FileSystemPath);
            var dest = Path.Combine(targetDirectory, name);
            try
            {
                dest = ResolveDestinationPath(dest, options.CollisionPolicy, ref skipped, ref failed, errors);
                if (dest is null)
                    continue;

                File.Copy(item.FileSystemPath, dest, overwrite: options.CollisionPolicy == NameCollisionPolicy.Overwrite);
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
