namespace MediaBrowser.Core.Models;

/// <summary>
/// 单条媒体项：文件系统路径或 MTP 对象（由 Id 与 SourceKind 区分）。
/// </summary>
public sealed class MediaItem
{
    public required string Id { get; init; }
    public required MediaSourceKind SourceKind { get; init; }
    public string DisplayName { get; init; } = "";
    /// <summary>文件系统上的完整路径；MTP 时为空。</summary>
    public string? FileSystemPath { get; init; }
    /// <summary>所在文件夹路径（展示或分组用）。</summary>
    public string? ContainingFolderPath { get; init; }
    /// <summary>用于时间线排序的 UTC 时间（EXIF 或修改时间）。</summary>
    public DateTime SortTimeUtc { get; init; }
    public bool IsVideo { get; init; }
    /// <summary>MTP：设备 PnP ID + 对象 ID 等，由 App 解释。</summary>
    public string? MtpObjectId { get; init; }
    public string? MtpDeviceId { get; init; }
}
