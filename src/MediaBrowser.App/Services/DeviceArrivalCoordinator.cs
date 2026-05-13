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

    /// <summary>
    /// 用户主动关闭窗口后被抑制的会话键集合，
    /// 只要设备仍处于连接状态就不会再次自动弹窗。
    /// 当设备真正被拔出时会自动从该集合中移除。
    /// 同时记录对应的 DeviceSessionDescriptor，方便"重新打开窗口"按钮直接复用。
    /// </summary>
    private readonly Dictionary<string, DeviceSessionDescriptor> _suppressedSessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一次 RefreshVolumes 枚举到的卷会话键集合，用于探测拔出。</summary>
    private HashSet<string> _lastVolumeSessionKeys = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 在窗口关闭时调用，将该会话键加入抑制集合，防止下一次轮询自动重弹。
    /// </summary>
    public void SuppressSession(DeviceSessionDescriptor descriptor)
    {
        if (descriptor == null || string.IsNullOrEmpty(descriptor.SessionKey))
            return;

        lock (_gate)
        {
            _suppressedSessions[descriptor.SessionKey] = descriptor;
            Debug.WriteLine($"[DeviceArrival] 抑制会话 '{descriptor.SessionKey}' ({descriptor.DisplayName})");
        }
    }

    /// <summary>查询某会话键当前是否处于抑制状态。</summary>
    public bool IsSuppressed(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
            return false;
        lock (_gate)
        {
            return _suppressedSessions.ContainsKey(sessionKey);
        }
    }

    /// <summary>解除某会话键的抑制状态。</summary>
    public void UnsuppressSession(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
            return;
        lock (_gate)
        {
            if (_suppressedSessions.Remove(sessionKey))
                Debug.WriteLine($"[DeviceArrival] 解除抑制 '{sessionKey}'");
        }
    }

    /// <summary>
    /// 获取当前所有"已连接但被抑制"的设备描述符，
    /// 供主窗口"重新打开窗口"按钮显示候选列表使用。
    /// </summary>
    public IReadOnlyList<DeviceSessionDescriptor> GetReopenableDevices()
    {
        lock (_gate)
        {
            return _suppressedSessions.Values.ToList();
        }
    }

    /// <summary>
    /// 兼容旧调用：以前用于在 MTP 窗口关闭后立刻清除快照以便再次自动弹出，
    /// 现在已由"抑制集合 + 真正拔出时清理"机制替代，因此此方法不再修改快照。
    /// 保留方法签名以避免破坏既有调用点。
    /// </summary>
    public void NotifyMtpSessionClosed(string deviceName)
    {
        // 不再从 _mtpSnapshot 移除，否则抑制会立即失效。
        // 真正的清理由 RefreshMtpDevices 在设备消失时完成。
        _ = deviceName;
    }

    /// <summary>当前已打开的可移动卷的 VolumeLabel 集合，用于排除 MTP 重复检测。</summary>
    private HashSet<string> _openVolumeLabels = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshVolumes()
    {
        var currentLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentSessionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            currentSessionKeys.Add(key);

            // 已打开则跳过
            if (MediaWindowRegistry.IsOpen(key))
                continue;

            // 被用户主动关闭抑制过：跳过自动弹窗
            if (IsSuppressed(key))
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

        // 探测被拔出的卷：上一轮存在但本轮缺失 → 解除抑制（让下次重新插入恢复自动弹窗）
        List<string>? removedKeys = null;
        lock (_gate)
        {
            foreach (var oldKey in _lastVolumeSessionKeys)
            {
                if (!currentSessionKeys.Contains(oldKey))
                {
                    (removedKeys ??= new List<string>()).Add(oldKey);
                }
            }
            _lastVolumeSessionKeys = currentSessionKeys;
            _openVolumeLabels = currentLabels;
        }

        if (removedKeys != null)
        {
            foreach (var key in removedKeys)
                UnsuppressSession(key);
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

        List<string>? removedSessionKeys = null;

        lock (_gate)
        {
            var current = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 探测被拔出的 MTP 设备：上一轮快照存在但本轮缺失 → 解除抑制
            foreach (var oldName in _mtpSnapshot)
            {
                if (!current.Contains(oldName))
                {
                    var oldKey = MediaWindowRegistry.BuildMtpSessionKey(oldName);
                    (removedSessionKeys ??= new List<string>()).Add(oldKey);
                }
            }

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

                // 被用户主动关闭抑制过：跳过自动弹窗（仍要把它放进 _mtpSnapshot 以便后续探测拔出）
                if (_suppressedSessions.ContainsKey(sessionKey))
                {
                    _mtpSnapshot.Add(name);
                    continue;
                }

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

        if (removedSessionKeys != null)
        {
            foreach (var key in removedSessionKeys)
                UnsuppressSession(key);
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
