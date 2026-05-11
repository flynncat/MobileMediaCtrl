using System.IO;
using System.Text.Json;
using System.Windows;

namespace MediaBrowser.App.Services;

/// <summary>
/// 管理应用程序的多语言切换。
/// 通过加载不同的 ResourceDictionary 实现 UI 文本的动态切换。
/// </summary>
public static class LanguageManager
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaBrowser", "settings.json");

    private static ResourceDictionary? _currentLangDict;

    /// <summary>支持的语言列表（显示名 → 资源文件名）。</summary>
    public static readonly (string DisplayName, string Code)[] SupportedLanguages =
    {
        ("中文", "zh-CN"),
        ("English", "en-US"),
    };

    /// <summary>当前语言代码。</summary>
    public static string CurrentLanguage { get; private set; } = "zh-CN";

    /// <summary>
    /// 在应用启动时调用，加载已保存的语言设置并应用。
    /// </summary>
    public static void Initialize()
    {
        var saved = LoadSavedLanguage();
        if (!string.IsNullOrEmpty(saved))
            CurrentLanguage = saved;
        ApplyLanguage(CurrentLanguage);
    }

    /// <summary>
    /// 切换到指定语言并保存设置。
    /// </summary>
    public static void SwitchLanguage(string langCode)
    {
        CurrentLanguage = langCode;
        ApplyLanguage(langCode);
        SaveLanguage(langCode);
    }

    /// <summary>
    /// 从当前语言资源中获取字符串。
    /// </summary>
    public static string GetString(string key)
    {
        if (System.Windows.Application.Current?.Resources[key] is string s)

            return s;
        return $"[{key}]";
    }

    /// <summary>
    /// 从当前语言资源中获取格式化字符串。
    /// </summary>
    public static string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    private static void ApplyLanguage(string langCode)
    {
        var uri = new Uri($"Languages/{langCode}.xaml", UriKind.Relative);
        ResourceDictionary newDict;
        try
        {
            newDict = new ResourceDictionary { Source = uri };
        }
        catch
        {
            // 回退到中文
            newDict = new ResourceDictionary
            {
                Source = new Uri("Languages/zh-CN.xaml", UriKind.Relative)
            };
        }

        var app = System.Windows.Application.Current;

        if (app == null) return;

        if (_currentLangDict != null)
            app.Resources.MergedDictionaries.Remove(_currentLangDict);

        app.Resources.MergedDictionaries.Add(newDict);
        _currentLangDict = newDict;
    }

    private static string? LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return null;
            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("language", out var prop))
                return prop.GetString();
        }
        catch
        {
            // 忽略读取失败
        }
        return null;
    }

    private static void SaveLanguage(string langCode)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { language = langCode });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 忽略保存失败
        }
    }
}
