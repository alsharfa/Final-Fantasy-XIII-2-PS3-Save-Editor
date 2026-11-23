using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FFXIII2SaveEditor;

// Language selector (Auto / Français / English) and re-application of translations.
public partial class MainWindow
{
    private bool _syncingLanguage;

    private void InitLanguage()
    {
        Lang.Init();
        SyncLanguageCombo();
        LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
        Loaded += (_, _) => ApplyLanguage();
        // Tabs create their content lazily: translate whenever a tab is shown.
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TabControl)
                Dispatcher.BeginInvoke(new Action(ApplyLanguage), System.Windows.Threading.DispatcherPriority.Loaded);
        }));
    }

    private void SyncLanguageCombo()
    {
        if (LanguageCombo is null) return;
        _syncingLanguage = true;
        LanguageCombo.SelectedIndex = Lang.Setting switch { "fr" => 1, "en" => 2, _ => 0 };
        if (LanguageCombo.Items[0] is ComboBoxItem auto)
            auto.Content = Lang.French ? "Auto (français)" : "Auto (English)";
        _syncingLanguage = false;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLanguage) return;
        string setting = LanguageCombo.SelectedIndex switch { 1 => "fr", 2 => "en", _ => Lang.Auto };
        Lang.SetSetting(setting, _session?.Language);
        SyncLanguageCombo();
        if (_plain is not null)
        {
            UpdateSaveInfo();
            try { RefreshSelectedTab(); } catch { }
        }
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        try { Lang.Apply(this); } catch { }
    }
}
