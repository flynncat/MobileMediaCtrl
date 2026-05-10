using MediaBrowser.Core.MediaFormats;
using MediaBrowser.Core.Models;
using MediaBrowser.Core.Services;

namespace MediaBrowser.Core.Catalog;

public sealed class FileSystemMediaCatalog
{
    public async Task<IReadOnlyList<MediaItem>> EnumerateAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            return Array.Empty<MediaItem>();

        return await Task.Run(() => EnumerateCore(rootDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static List<MediaItem> EnumerateCore(string rootDirectory, CancellationToken cancellationToken)
    {
        var list = new List<MediaItem>();
        var stack = new Stack<string>();
        stack.Push(rootDirectory);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MediaExtensionLists.IsMediaFile(file))
                    continue;

                DateTime sortUtc;
                try
                {
                    var taken = PhotoMetadataReader.TryGetDateTimeOriginalUtc(file);
                    if (taken.HasValue)
                        sortUtc = taken.Value;
                    else
                        sortUtc = File.GetLastWriteTimeUtc(file);
                }
                catch
                {
                    sortUtc = DateTime.UtcNow;
                }

                var folder = Path.GetDirectoryName(file);
                list.Add(new MediaItem
                {
                    Id = file,
                    SourceKind = MediaSourceKind.FileSystem,
                    FileSystemPath = file,
                    DisplayName = Path.GetFileName(file),
                    ContainingFolderPath = folder,
                    SortTimeUtc = sortUtc,
                    IsVideo = MediaExtensionLists.IsVideo(file),
                });
            }

            foreach (var sub in subdirs)
                stack.Push(sub);
        }

        return list;
    }
}
