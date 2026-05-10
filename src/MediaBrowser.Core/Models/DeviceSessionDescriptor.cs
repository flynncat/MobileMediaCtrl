namespace MediaBrowser.Core.Models;

public enum DeviceKind
{
    RemovableVolume = 0,
    MtpDevice = 1,
}

/// <summary>用于打开媒体窗口的设备会话描述。</summary>
public sealed class DeviceSessionDescriptor
{
    public required string SessionKey { get; init; }
    public DeviceKind Kind { get; init; }
    /// <summary>U 盘/可移动卷根路径，如 E:\</summary>
    public string? VolumeRootPath { get; init; }
    /// <summary>MTP 设备 PnP 设备 ID。</summary>
    public string? MtpDeviceId { get; init; }
    public string DisplayName { get; init; } = "";
}
