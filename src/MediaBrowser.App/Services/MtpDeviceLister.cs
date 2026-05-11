using MediaDevices;

namespace MediaBrowser.App.Services;

/// <summary>
/// 封装 MTP 设备的发现与连接。
/// 注意：MediaDevices 库的 PnPDeviceID 属性必须在 Connect() 之后才能访问，
/// 因此使用 FriendlyName 作为设备发现阶段的标识键。
/// </summary>
public static class MtpDeviceLister
{
    /// <summary>
    /// 返回当前可见的 MTP 设备的 FriendlyName 列表（无需 Connect）。
    /// </summary>
    public static List<string> GetMtpDeviceNames()
    {
        try
        {
            return MediaDevice.GetDevices()
                .Select(d =>
                {
                    try { return d.FriendlyName; }
                    catch { return null; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 通过 FriendlyName 查找设备并建立只读连接。
    /// 调用方负责在使用完毕后调用 Disconnect()。
    /// </summary>
    public static MediaDevice? TryConnectByName(string friendlyName)
    {
        try
        {
            var device = MediaDevice.GetDevices()
                .FirstOrDefault(d =>
                {
                    try { return string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });

            if (device is null)
                return null;

            device.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Read, false);
            return device;
        }
        catch
        {
            return null;
        }
    }
}