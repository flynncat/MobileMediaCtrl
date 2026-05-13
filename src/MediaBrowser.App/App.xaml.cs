using System.Windows;
using MediaBrowser.App.Services;

namespace MediaBrowser.App;

public partial class App : System.Windows.Application

{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.LanguageManager.Initialize();
        ApplicationSession.Initialize();

        // 主窗口显示后，检查是否首次运行并询问开机自启动
        Dispatcher.BeginInvoke(new Action(CheckFirstRunAutoStart),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 首次运行时，弹出一次性确认对话框询问是否启用开机自启动。
    /// 无论用户选择什么，都将 IsFirstRun 置为 false 持久化，确保后续不再弹出。
    /// </summary>
    private static void CheckFirstRunAutoStart()
    {
        var settings = SettingsStore.Load();
        if (!settings.IsFirstRun)
            return;

        try
        {
            var result = System.Windows.MessageBox.Show(
                LanguageManager.GetString("FirstRun_Message"),
                LanguageManager.GetString("FirstRun_Title"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)

            {
                if (AutoStartManager.Enable())
                    settings.AutoStart = true;
            }
        }
        finally
        {
            // 无论结果如何，都标记为非首次运行
            settings.IsFirstRun = false;
            SettingsStore.Save(settings);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApplicationSession.Shutdown();
        base.OnExit(e);
    }
}
