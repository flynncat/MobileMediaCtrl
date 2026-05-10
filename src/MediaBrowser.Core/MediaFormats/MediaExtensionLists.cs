using System.Collections.Generic;
using System.IO;

namespace MediaBrowser.Core.MediaFormats;

/// <summary>
/// Default extensions for images and videos scanned from removable storage or MTP paths.
/// </summary>
public static class MediaExtensionLists
{
    public static readonly HashSet<string> DefaultImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff",
    };

    public static readonly HashSet<string> DefaultVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".3gp", ".3g2", ".mts", ".m2ts",
    };

    public static bool IsMediaFile(string filePath, HashSet<string>? images = null, HashSet<string>? videos = null)
    {
        images ??= DefaultImageExtensions;
        videos ??= DefaultVideoExtensions;
        var ext = Path.GetExtension(filePath);
        return images.Contains(ext) || videos.Contains(ext);
    }

    public static bool IsVideo(string filePath, HashSet<string>? videos = null)
    {
        videos ??= DefaultVideoExtensions;
        return videos.Contains(Path.GetExtension(filePath));
    }
}
