using System.Windows;
using MediaBrowser.App.Services;

namespace MediaBrowser.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadLanguageOptions();
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is LanguageItem selected)
        {
            LanguageManager.SwitchLanguage(selected.Code);
        }
        Close();
    }

    private sealed record LanguageItem(string DisplayName, string Code)
    {
        public override string ToString() => DisplayName;
    }
}
