using System.Windows.Controls;
using Kil0bitSystemMonitor.Services.Localization;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Kil0bitSystemMonitor;

public partial class SettingsWindow
{
    private bool _loadingAppLanguage;

    private void InitializeLanguageSettings()
    {
        if (FindName("AppLanguageCombo") is not ComboBox combo) return;

        _loadingAppLanguage = true;
        try
        {
            string selected = _config.Config.AppLanguage;
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem option && string.Equals(option.Tag as string, selected, StringComparison.Ordinal))
                {
                    combo.SelectedItem = option;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }
        finally
        {
            _loadingAppLanguage = false;
        }
    }

    private void AppLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAppLanguage || sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem { Tag: string language }) return;

        _config.Config.AppLanguage = language;
        UiText.SetLanguage(_config.Config.AppLanguage);
        RefreshLocalizedSettingsText();
        _config.SaveConfig();
    }
}
