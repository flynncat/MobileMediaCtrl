using System.Diagnostics;
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
    private HashSet<string> _mtpSnapshot = new(StringComparer.OrdinalIgnoreCase);

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
    public void NotifyMtpSessionClosed(string deviceName)
    {
        lock (_gate)
        {
            _mtpSnapshot.Remove(deviceName);
        }
    }

    /// <summary>当前已打开的可移动卷的 VolumeLabel 集合，用于排除 MTP 重复检测。</summary>
    private HashSet<string> _openVolumeLabels = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshVolumes()
    {
        var currentLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable)
                continue;
            if (!drive.IsReady)
                continue;

            // 记录卷标签，用于后续排除 MTP 重复
            try
            {
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    currentLabels.Add(drive.VolumeLabel);
            }
            catch { /* 某些驱动器可能无法读取 VolumeLabel */ }

            var root = drive.RootDirectory.FullName;
            var key = MediaWindowRegistry.BuildVolumeSessionKey(root);
            if (MediaWindowRegistry.IsOpen(key))
                continue;

            // 使用卷标签作为显示名（如果有），否则使用盘符
            string displayName;
            try
            {
                displayName = !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})"
                    : LanguageManager.GetString("Device_UsbDrive", drive.Name.TrimEnd('\\'));
            }
            catch
            {
                displayName = LanguageManager.GetString("Device_UsbDrive", drive.Name.TrimEnd('\\'));
            }

            var desc = new DeviceSessionDescriptor
            {
                SessionKey = key,
                Kind = DeviceKind.RemovableVolume,
                VolumeRootPath = root,
                DisplayName = displayName,
            };
            MediaWindowFactory.OpenForDevice(desc);
        }

        lock (_gate)
        {
            _openVolumeLabels = currentLabels;
        }
    }


    private void RefreshMtpDevices()
    {
        List<string> names;
        try
        {
            names = MtpDeviceLister.GetMtpDeviceNames();
            Debug.WriteLine($"[DeviceArrival] MTP 枚举到 {names.Count} 个设备: {string.Join("; ", names)}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceArrival] MTP 枚举异常: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            var current = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var name in current)
            {
                if (_mtpSnapshot.Contains(name))
                    continue;

                // 如果该 MTP 设备名称与已打开的可移动卷标签匹配，跳过（避免重复弹窗）
                if (_openVolumeLabels.Contains(name))
                {
                    Debug.WriteLine($"[DeviceArrival] 跳过 MTP 设备 '{name}'：已作为可移动卷打开");
                    _mtpSnapshot.Add(name);
                    continue;
                }

                // 额外检查：如果设备名称与当前任何可移动卷的盘符匹配，也跳过
                if (IsLikelyAlreadyMountedAsVolume(name))
                {
                    Debug.WriteLine($"[DeviceArrival] 跳过 MTP 设备 '{name}'：疑似已挂载为可移动卷");
                    _mtpSnapshot.Add(name);
                    continue;
                }

                var sessionKey = MediaWindowRegistry.BuildMtpSessionKey(name);
                if (MediaWindowRegistry.IsOpen(sessionKey))
                    continue;

                var desc = new DeviceSessionDescriptor
                {
                    SessionKey = sessionKey,
                    Kind = DeviceKind.MtpDevice,
                    MtpDeviceId = name,
                    DisplayName = name,
                };
                MediaWindowFactory.OpenForDevice(desc);
            }

            _mtpSnapshot = current;
        }
    }

    /// <summary>
    /// 判断 MTP 设备名称是否可能已经作为可移动卷挂载。
    /// 通过检查当前所有可移动卷的 VolumeLabel 是否包含该名称（或反向包含）来判断。
    /// </summary>
    private bool IsLikelyAlreadyMountedAsVolume(string mtpDeviceName)
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                    continue;

                try
                {
                    var label = drive.VolumeLabel;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    // 精确匹配或包含关系
                    if (string.Equals(label, mtpDeviceName, StringComparison.OrdinalIgnoreCase) ||
                        mtpDeviceName.Contains(label, StringComparison.OrdinalIgnoreCase) ||
                        label.Contains(mtpDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch { /* 忽略单个驱动器读取失败 */ }
            }
        }
        catch { /* 忽略 */ }

        return false;
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
