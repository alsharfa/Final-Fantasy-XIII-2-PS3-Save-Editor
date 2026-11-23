using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Globalization;
using System.IO;

namespace FFXIII2SaveEditor;

// Language selector (Auto / Français / English) and re-application of translations.
public partial class MainWindow
{
    private bool _syncingLanguage;

    private void InitLanguage()
    {
        Lang.Init();
        GameNames.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "names_fr.tsv"));
        AttachGameNameConverters(this);
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

    private static readonly System.Collections.Generic.HashSet<string> NameColumnPaths = new(StringComparer.Ordinal)
    {
        "Name", "Role", "Type", "Category", "SourcePassive", "Mapping",
    };

    // Wrap name-like DataGrid text column bindings with a display-only translator for game names.
    private static void AttachGameNameConverters(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DataGrid grid)
            {
                foreach (DataGridColumn column in grid.Columns)
                {
                    if (column is DataGridTextColumn text && text.Binding is Binding b && b.Converter is null
                        && b.Path is not null && NameColumnPaths.Contains(b.Path.Path))
                    {
                        // Name-like, read-only display columns only: values are translated, never written back.
                        text.IsReadOnly = true;
                        text.Binding = new Binding
                        {
                            Path = b.Path,
                            Mode = BindingMode.OneWay,
                            StringFormat = b.StringFormat,
                            Converter = GameNameConverter.Instance,
                        };
                    }
                }
            }
            if (child is DependencyObject d) AttachGameNameConverters(d);
        }
    }

    private void ApplyLanguage()
    {
        try { Lang.Apply(this); } catch { }
    }
}

public sealed class GameNameConverter : IValueConverter
{
    public static readonly GameNameConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && Lang.French ? GameNames.ToFrench(s) : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
