namespace MediaBrowser.App.Services;

public static class ApplicationSession
{
    public static DeviceArrivalCoordinator Coordinator { get; } = new();

    public static void Initialize()
    {
        Coordinator.Start();
    }

    public static void Shutdown()
    {
        Coordinator.Dispose();
    }
}
