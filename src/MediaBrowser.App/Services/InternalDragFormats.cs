namespace MediaBrowser.App.Services;

public static class InternalDragFormats
{
    public const string MediaItems = "MediaBrowser.MediaItems.v1";
    /// <summary>拖拽源窗口的实例标识，用于检测拖拽到自身窗口的误操作。</summary>
    public const string SourceWindowId = "MediaBrowser.SourceWindowId.v1";

    // ── 进程内拖放状态（避免将自定义数据放入 DataObject 导致跨进程 OLE 序列化失败） ──

    /// <summary>当前正在进行拖放操作的源窗口 ID（进程内静态变量）。</summary>
    public static string? ActiveDragSourceWindowId { get; set; }

    /// <summary>当前拖放操作携带的媒体项 JSON（进程内静态变量）。</summary>
    public static string? ActiveDragMediaItemsJson { get; set; }

    /// <summary>清除拖放状态。</summary>
    public static void ClearDragState()
    {
        ActiveDragSourceWindowId = null;
        ActiveDragMediaItemsJson = null;
    }
}



public sealed class MediaDragRecord
{
    public string Kind { get; init; } = ""; // fs | mtp
    public string? FsPath { get; init; }
    public string? PnpId { get; init; }
    public string? MtpPath { get; init; }
    public string DisplayName { get; init; } = "";
}
