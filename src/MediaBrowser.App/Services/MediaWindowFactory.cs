using System.Windows;
using MediaBrowser.Core.Models;
using MediaBrowser.App.Views;

namespace MediaBrowser.App.Services;

public static class MediaWindowFactory
{
    public static void OpenForDevice(DeviceSessionDescriptor descriptor)
    {
        void Open()
        {
            if (MediaWindowRegistry.IsOpen(descriptor.SessionKey))
                return;

            var win = new MediaWindow(descriptor);
            if (!MediaWindowRegistry.TryRegister(descriptor.SessionKey, win))
            {
                win.Close();
                return;
            }

            win.Closed += (_, _) =>
            {
                MediaWindowRegistry.Unregister(descriptor.SessionKey);
                // 用户主动关闭后将该会话标记为已抑制，
                // 防止下一次轮询立即重新弹窗。设备真正拔出时会自动解除。
                ApplicationSession.Coordinator.SuppressSession(descriptor);
            };
            win.Show();

        }

        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            Open();
        else
            System.Windows.Application.Current?.Dispatcher.Invoke(Open);

    }
}
