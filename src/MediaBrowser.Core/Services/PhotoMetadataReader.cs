using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace MediaBrowser.Core.Services;

public static class PhotoMetadataReader
{
    /// <summary>尝试读取拍摄时间（UTC）；失败返回 null。</summary>
    public static DateTime? TryGetDateTimeOriginalUtc(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var local))
                return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

            var exif = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (exif != null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
        }
        catch
        {
            // 非图片或损坏时忽略
        }

        return null;
    }
}
