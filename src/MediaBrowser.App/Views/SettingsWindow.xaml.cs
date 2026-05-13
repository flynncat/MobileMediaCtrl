using System.Windows;
using MediaBrowser.App.Services;

namespace MediaBrowser.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadLanguageOptions();
        LoadAutoStartState();
    }

    private void LoadLanguageOptions()
    {
        foreach (var (displayName, code) in LanguageManager.SupportedLanguages)
        {
            LanguageCombo.Items.Add(new LanguageItem(displayName, code));
        }

        // 选中当前语言
        for (var i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is LanguageItem item && item.Code == LanguageManager.CurrentLanguage)
            {
                LanguageCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void LoadAutoStartState()
    {
        // 复选框反映注册表实际状态
        AutoStartCheckBox.IsChecked = AutoStartManager.IsEnabled();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 1. 切换语言
        if (LanguageCombo.SelectedItem is LanguageItem selected)
        {
            LanguageManager.SwitchLanguage(selected.Code);
        }

        // 2. 应用开机自启动设置
        var wantAutoStart = AutoStartCheckBox.IsChecked == true;
        var success = wantAutoStart ? AutoStartManager.Enable() : AutoStartManager.Disable();

        // 3. 持久化到 settings.json（语言已在 SwitchLanguage 中写入，这里同步 AutoStart）
        var settings = SettingsStore.Load();
        settings.AutoStart = wantAutoStart && success;
        SettingsStore.Save(settings);

        // 4. 注册表写入失败时提示
        if (!success)
        {
            System.Windows.MessageBox.Show(
                LanguageManager.GetString("Settings_AutoStartFailed"),
                LanguageManager.GetString("Settings_Title"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }


        Close();
    }

    private sealed record LanguageItem(string DisplayName, string Code)
    {
        public override string ToString() => DisplayName;
    }
}
