using System;
using System.IO;

namespace MediaBrowser.App.Services;

/// <summary>
/// 拖拽诊断日志：把关键事件写到 %TEMP%/MediaBrowserDragLog/drag-yyyyMMdd.log
/// 用于排查"未预览的视频拖拽生成 0 字节文件"等难以现场观察的问题。
/// 简单起见使用进程级单例，所有写入加锁串行化。
/// </summary>
internal static class DragDiagLogger
{
    private static readonly object s_lock = new();
    private static string? s_logPath;

    private static string EnsureLogPath()
    {
        if (s_logPath != null) return s_logPath;

        var dir = Path.Combine(Path.GetTempPath(), "MediaBrowserDragLog");
        try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
        s_logPath = Path.Combine(dir, $"drag-{DateTime.Now:yyyyMMdd}.log");
        return s_logPath;
    }

    /// <summary>记录一行日志。多线程安全。失败时静默忽略。</summary>
    public static void Log(string category, string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{Environment.CurrentManagedThreadId}] [{category}] {message}{Environment.NewLine}";
            lock (s_lock)
            {
                File.AppendAllText(EnsureLogPath(), line);
            }
        }
        catch
        {
            // 日志写入失败不应影响主流程
        }
    }

    /// <summary>记录一个异常（含堆栈）。</summary>
    public static void LogError(string category, string message, Exception ex)
    {
        Log(category, $"{message} :: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>返回日志文件路径（首次调用时创建）。</summary>
    public static string GetLogFilePath() => EnsureLogPath();
}
