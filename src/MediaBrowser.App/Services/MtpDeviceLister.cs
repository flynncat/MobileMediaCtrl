using MediaDevices;

namespace MediaBrowser.App.Services;

public static class MtpDeviceLister
{
    public static List<string> GetPortableDevicePnPIds()
    {
        try
        {
            return MediaDevice.GetDevices()
                .Select(d => d.PnPDeviceID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static string? TryGetFriendlyName(string pnpDeviceId)
    {
        try
        {
            var d = MediaDevice.GetDevices()
                .FirstOrDefault(x => string.Equals(x.PnPDeviceID, pnpDeviceId, StringComparison.Ordinal));
            return d?.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    public static MediaDevice? TryFindByPnPId(string pnpDeviceId)
    {
        try
        {
            return MediaDevice.GetDevices()
                .FirstOrDefault(x => string.Equals(x.PnPDeviceID, pnpDeviceId, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }
}
