using System.Windows;
using MediaBrowser.App.Services;

namespace MediaBrowser.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationSession.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApplicationSession.Shutdown();
        base.OnExit(e);
    }
}
