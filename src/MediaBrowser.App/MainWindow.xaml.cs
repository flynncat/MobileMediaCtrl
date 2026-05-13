using System.Windows;
using System.Windows.Controls;
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

        // 动态构造 ContextMenu，每个 MenuItem 对应一个被抑制的设备
        var menu = new ContextMenu();
        foreach (var desc in devices)
        {
            var item = new MenuItem { Header = desc.DisplayName, Tag = desc };
            item.Click += (_, _) =>
            {
                ApplicationSession.Coordinator.UnsuppressSession(desc.SessionKey);
                MediaWindowFactory.OpenForDevice(desc);
            };
            menu.Items.Add(item);
        }

        ReopenButton.ContextMenu = menu;
        menu.PlacementTarget = ReopenButton;
        menu.IsOpen = true;
    }
}
