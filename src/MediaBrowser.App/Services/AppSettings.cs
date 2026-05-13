namespace MediaBrowser.App.Services;

/// <summary>
/// 应用程序的持久化用户设置。
/// 通过 <see cref="SettingsStore"/> 在 %LocalAppData%\MediaBrowser\settings.json 中读写。
/// </summary>
public sealed class AppSettings
{
    /// <summary>界面语言代码，对应 Languages 文件夹下的资源文件名（如 zh-CN、en-US）。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>是否随 Windows 开机自启动。</summary>
    public bool AutoStart { get; set; }

    /// <summary>是否为首次运行（用于触发一次性开机自启动询问）。默认 true，首次运行后置为 false。</summary>
    public bool IsFirstRun { get; set; } = true;
}
