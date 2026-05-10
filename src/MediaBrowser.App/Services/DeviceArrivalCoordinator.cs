using System.IO;
using System.Management;
using System.Windows.Threading;
using MediaBrowser.Core.Models;

namespace MediaBrowser.App.Services;

/// <summary>
/// 监听卷变更与周期性刷新 MTP 设备列表，在检测到新会话时打开媒体窗口。
/// </summary>
public sealed class DeviceArrivalCoordinator : IDisposable
{
    private readonly object _gate = new();
    private ManagementEventWatcher? _volumeWatcher;
    private DispatcherTimer? _mtpPoll;
    private HashSet<string> _mtpSnapshot = new(StringComparer.Ordinal);
    private bool _started;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;

            try
            {
                var query = new WqlEventQuery(
                    "SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
                _volumeWatcher = new ManagementEventWatcher(query);
                _volumeWatcher.EventArrived += (_, _) => Dispatcher.CurrentDispatcher.BeginInvoke(RefreshVolumes);
                _volumeWatcher.Start();
            }
            catch
            {
                // WMI 不可用时仍可依赖轮询
            }

            _mtpPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _mtpPoll.Tick += (_, _) =>
            {
                RefreshVolumes();
                RefreshMtpDevices();
            };
            _mtpPoll.Start();

            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                RefreshVolumes();
                RefreshMtpDevices();
            });
        }
    }

    /// <summary>在关闭 MTP 窗口后调用，以便在设备仍连接时可再次自动打开。</summary>
    public void NotifyMtpSessionClosed(string pnpDeviceId)
    {
        lock (_gate)
        {
            _mtpSnapshot.Remove(pnpDeviceId);
        }
    }

    private void RefreshVolumes()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable)
                continue;
            if (!drive.IsReady)
                continue;

            var root = drive.RootDirectory.FullName;
            var key = MediaWindowRegistry.BuildVolumeSessionKey(root);
            if (MediaWindowRegistry.IsOpen(key))
                continue;

            var desc = new DeviceSessionDescriptor
            {
                SessionKey = key,
                Kind = DeviceKind.RemovableVolume,
                VolumeRootPath = root,
                DisplayName = $"U 盘 ({drive.Name.TrimEnd('\\')})",
            };
            MediaWindowFactory.OpenForDevice(desc);
        }
    }

    private void RefreshMtpDevices()
    {
        List<string> ids;
        try
        {
            ids = MtpDeviceLister.GetPortableDevicePnPIds();
        }
        catch
        {
            return;
        }

        lock (_gate)
        {
            var current = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var id in current)
            {
                if (_mtpSnapshot.Contains(id))
                    continue;

                var sessionKey = MediaWindowRegistry.BuildMtpSessionKey(id);
                if (MediaWindowRegistry.IsOpen(sessionKey))
                    continue;

                var name = MtpDeviceLister.TryGetFriendlyName(id) ?? "手机/便携设备";
                var desc = new DeviceSessionDescriptor
                {
                    SessionKey = sessionKey,
                    Kind = DeviceKind.MtpDevice,
                    MtpDeviceId = id,
                    DisplayName = name,
                };
                MediaWindowFactory.OpenForDevice(desc);
            }

            _mtpSnapshot = current;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _volumeWatcher?.Stop();
            _volumeWatcher?.Dispose();
            _volumeWatcher = null;

            _mtpPoll?.Stop();
            _mtpPoll = null;
        }
    }
}
