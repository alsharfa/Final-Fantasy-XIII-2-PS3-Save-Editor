using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FFXIII2SaveEditor;

// Lightweight UI localisation. English is the source language (all strings in XAML/code stay
// English). When French is active, visible texts are looked up in Assets/lang_fr.tsv:
//   exact lines:    English<TAB>Français
//   pattern lines:  English with {*} wildcards<TAB>Français with {0},{1}... in the same order
// Game data names (items, monsters, abilities) are NOT translated here: editor logic matches
// on their English names.
public static class Lang
{
    public const string Auto = "auto";
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<(Regex Pattern, string Fr)> Patterns = new();
    private static readonly HashSet<string> FrValues = new(StringComparer.Ordinal);
    private static bool _loaded;

    // Original English text per element/property, so switching back to English works.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, string>> Originals = new();

    public static bool French { get; private set; }
    public static string Setting { get; private set; } = Auto;

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FFXIII2SaveEditor", "lang.txt");

    public static void Init()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string s = File.ReadAllText(SettingsFile).Trim().ToLowerInvariant();
                if (s is "fr" or "en" or Auto) Setting = s;
            }
        }
        catch { }
        Resolve(null);
    }

    public static void SetSetting(string setting, string? saveLanguage)
    {
        Setting = setting;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, setting);
        }
        catch { }
        Resolve(saveLanguage);
    }

    // Auto: language of the loaded save (from PARAM.SFO), otherwise the Windows UI language.
    public static void Resolve(string? saveLanguage)
    {
        string lang = Setting;
        if (lang == Auto)
        {
            lang = !string.IsNullOrEmpty(saveLanguage)
                ? saveLanguage
                : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }
        French = lang == "fr";
        if (French) EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "lang_fr.tsv");
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw.StartsWith('#')) continue;
                int tab = raw.IndexOf('\t');
                if (tab <= 0) continue;
                string en = Unescape(raw[..tab]);
                string fr = Unescape(raw[(tab + 1)..]);
                if (en.Contains("{*}"))
                {
                    string rx = "^" + string.Join("(.*?)", en.Split("{*}").Select(Regex.Escape)) + "$";
                    Patterns.Add((new Regex(rx, RegexOptions.Singleline | RegexOptions.CultureInvariant), fr));
                }
                else
                {
                    Exact[en] = fr;
                }
                FrValues.Add(fr);
            }
        }
        catch { }
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");

    public static string T(string? en)
    {
        if (string.IsNullOrEmpty(en) || !French) return en ?? "";
        if (Exact.TryGetValue(en, out string? fr)) return fr;
        string trimmed = en.Trim();
        if (trimmed.Length != en.Length && Exact.TryGetValue(trimmed, out fr))
            return en.Replace(trimmed, fr);
        foreach (var (pattern, frTemplate) in Patterns)
        {
            Match m = pattern.Match(en);
            if (!m.Success) continue;
            string result = frTemplate;
            for (int i = 1; i < m.Groups.Count; i++)
                result = result.Replace("{" + (i - 1) + "}", T(m.Groups[i].Value));
            return result;
        }
        // Composite messages "A - B" / "A\n\nB": translate the known parts.
        foreach (string sep in new[] { "\n\n", " - ", "\n" })
        {
            int idx = en.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && idx < en.Length - sep.Length)
            {
                string a = en[..idx], b = en[(idx + sep.Length)..];
                string ta = T(a), tb = T(b);
                if (ta != a || tb != b) return ta + sep + tb;
            }
        }
        return en;
    }

    // Walks the window's logical tree (plus DataGrid/GridView headers and tooltips) and swaps
    // static English texts with French ones, or restores the English originals.
    public static void Apply(DependencyObject root) => Walk(root, 0);

    private static void Walk(object node, int depth)
    {
        if (depth > 200 || node is null) return;
        if (node is FrameworkElement fe && fe.Tag is string tag && tag == "notr") return;

        switch (node)
        {
            case TextBlock tb when tb.Inlines.Count <= 1 && !BindingOperations.IsDataBound(tb, TextBlock.TextProperty):
                tb.Text = Swap(tb, "Text", tb.Text);
                break;
            case TextBox:
                return; // user data, never translated
            case HeaderedContentControl hcc:
                if (hcc.Header is string h && !BindingOperations.IsDataBound(hcc, HeaderedContentControl.HeaderProperty)) hcc.Header = Swap(hcc, "Header", h);
                if (hcc.Content is string hc && !BindingOperations.IsDataBound(hcc, ContentControl.ContentProperty)) hcc.Content = Swap(hcc, "Content", hc);
                break;
            case HeaderedItemsControl hic:
                if (hic.Header is string hh) hic.Header = Swap(hic, "Header", hh);
                break;
            case ContentControl cc when cc.Content is string c && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty):
                cc.Content = Swap(cc, "Content", c);
                break;
        }

        if (node is FrameworkElement el && el.ToolTip is string tip)
            el.ToolTip = Swap(el, "ToolTip", tip);

        if (node is DataGrid dg)
        {
            foreach (DataGridColumn col in dg.Columns)
                if (col.Header is string ch) col.Header = Swap(col, "Header", ch);
        }
        if (node is ListView lv && lv.View is GridView gv)
        {
            foreach (GridViewColumn col in gv.Columns)
                if (col.Header is string ch) col.Header = Swap(col, "Header", ch);
        }
        if (node is DataGrid or ListView or ListBox or ComboBox) return; // data rows

        if (node is DependencyObject d)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                Walk(child, depth + 1);
        }
    }

    private static string Swap(object owner, string prop, string current)
    {
        if (string.IsNullOrEmpty(current)) return current;
        var map = Originals.GetOrCreateValue(owner);
        map.TryGetValue(prop, out string? original);
        string? lastFr = map.TryGetValue(prop + "#fr", out string? f) ? f : null;

        // Text changed by code since our last pass: it is a fresh English source.
        if (original is null || (current != original && current != lastFr))
            original = current;

        map[prop] = original;
        if (!French) return original;
        string translated = T(original);
        map[prop + "#fr"] = translated;
        return translated;
    }
}
