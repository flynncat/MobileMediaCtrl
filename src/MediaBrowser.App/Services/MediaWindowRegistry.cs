using System.Collections.Concurrent;
using MediaBrowser.Core.Models;

namespace MediaBrowser.App.Services;

/// <summary>单进程内跟踪已打开的媒体窗口（按会话键去重）。</summary>
public static class MediaWindowRegistry
{
    private static readonly ConcurrentDictionary<string, Views.MediaWindow> OpenWindows = new();

    public static bool TryRegister(string sessionKey, Views.MediaWindow window) =>
        OpenWindows.TryAdd(sessionKey, window);

    public static void Unregister(string sessionKey) =>
        OpenWindows.TryRemove(sessionKey, out _);

    public static bool IsOpen(string sessionKey) => OpenWindows.ContainsKey(sessionKey);

    public static Views.MediaWindow? TryGet(string sessionKey)
    {
        OpenWindows.TryGetValue(sessionKey, out var w);
        return w;
    }

    public static string BuildVolumeSessionKey(string rootPath)
    {
        var letter = rootPath.TrimEnd('\\').ToUpperInvariant();
        return "vol:" + letter;
    }

    public static string BuildMtpSessionKey(string deviceId) => "mtp:" + deviceId;
}
