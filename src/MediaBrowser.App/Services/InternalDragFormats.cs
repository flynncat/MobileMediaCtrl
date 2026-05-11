namespace MediaBrowser.App.Services;

public static class InternalDragFormats
{
    public const string MediaItems = "MediaBrowser.MediaItems.v1";
    /// <summary>拖拽源窗口的实例标识，用于检测拖拽到自身窗口的误操作。</summary>
    public const string SourceWindowId = "MediaBrowser.SourceWindowId.v1";
}


public sealed class MediaDragRecord
{
    public string Kind { get; init; } = ""; // fs | mtp
    public string? FsPath { get; init; }
    public string? PnpId { get; init; }
    public string? MtpPath { get; init; }
    public string DisplayName { get; init; } = "";
}
