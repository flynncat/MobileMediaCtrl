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

            win.Closed += (_, _) => MediaWindowRegistry.Unregister(descriptor.SessionKey);
            win.Show();
        }

        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            Open();
        else
            System.Windows.Application.Current?.Dispatcher.Invoke(Open);

    }
}
