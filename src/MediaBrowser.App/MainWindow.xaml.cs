using System.Windows;
using MediaBrowser.App.Services;
using MediaBrowser.App.Views;

namespace MediaBrowser.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
    }

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        var devices = ApplicationSession.Coordinator.GetReopenableDevices();

        if (devices.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                LanguageManager.GetString("MainWindow_NoReopenable"),
                LanguageManager.GetString("MainWindow_ReopenTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // 直接打开所有被抑制的设备窗口；
        // 如果窗口已处于打开状态（理论上不会出现在抑制集合中，但仍做防御），
        // MediaWindowFactory.OpenForDevice 内部的 TryRegister 失败会自动跳过。
        foreach (var desc in devices)
        {
            // 先解除抑制，避免新窗口关闭事件触发的 SuppressSession 之前
            // 这条记录还残留在集合中
            ApplicationSession.Coordinator.UnsuppressSession(desc.SessionKey);

            if (MediaWindowRegistry.IsOpen(desc.SessionKey))
                continue;

            MediaWindowFactory.OpenForDevice(desc);
        }
    }
}
