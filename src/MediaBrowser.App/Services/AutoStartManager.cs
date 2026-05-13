using System.Diagnostics;
using Microsoft.Win32;

namespace MediaBrowser.App.Services;

/// <summary>
/// 管理应用程序的开机自启动状态。
/// 通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表实现，无需管理员权限。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "MediaBrowser";

    /// <summary>
    /// 获取当前 exe 的实际路径（用于注册表写入）。
    /// 优先使用 <see cref="Environment.ProcessPath"/> ，回退到主模块文件名。
    /// </summary>
    private static string? GetCurrentExecutablePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                return path;

            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查当前是否启用了开机自启动。
    /// 仅当注册表中存在 MediaBrowser 项且值与当前 exe 路径一致时，才视为已启用。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key == null) return false;

            var value = key.GetValue(AppRegistryName) as string;
            if (string.IsNullOrEmpty(value)) return false;

            var current = GetCurrentExecutablePath();
            if (string.IsNullOrEmpty(current)) return true; // 无法判定时按存在算启用

            // 注册表值可能带引号
            var trimmed = value.Trim().Trim('"');
            return string.Equals(trimmed, current, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启用开机自启动，将当前 exe 路径写入注册表。
    /// </summary>
    /// <returns>是否成功。</returns>
    public static bool Enable()
    {
        try
        {
            var path = GetCurrentExecutablePath();
            if (string.IsNullOrEmpty(path)) return false;

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return false;

            // 路径可能含空格，加双引号更稳妥
            key.SetValue(AppRegistryName, $"\"{path}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 禁用开机自启动，从注册表中移除对应键。
    /// </summary>
    /// <returns>是否成功（值原本不存在也视为成功）。</returns>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return true;

            if (key.GetValue(AppRegistryName) != null)
                key.DeleteValue(AppRegistryName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
