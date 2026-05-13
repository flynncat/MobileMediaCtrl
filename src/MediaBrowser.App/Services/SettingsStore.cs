using System.IO;
using System.Text.Json;

namespace MediaBrowser.App.Services;

/// <summary>
/// 用户设置的统一持久化存储。
/// 所有读写都使用 %LocalAppData%\MediaBrowser\settings.json 作为唯一路径。
/// </summary>
public static class SettingsStore
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaBrowser");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 加载用户设置。文件不存在或解析失败时返回默认值，不会抛出异常。
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            // 解析失败时静默回退默认值
            return new AppSettings();
        }
    }

    /// <summary>
    /// 保存用户设置到磁盘。写入失败时静默忽略，不会抛出异常。
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 写入失败时静默忽略，不影响应用使用
        }
    }
}
