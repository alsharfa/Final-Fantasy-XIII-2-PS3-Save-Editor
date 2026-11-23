using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FFXIII2SaveEditor;

// Display-only French names for game data (abilities, monsters, roles, some passives).
// Editor logic always works on the English names from the TSV assets; this is applied only
// when a value is shown in a table (see LanguageUi.cs) and when French is active.
public static class GameNames
{
    private static readonly Dictionary<string, string> Fr = new(StringComparer.Ordinal);
    private static readonly List<(Regex Pattern, string Template)> Patterns = new();
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fire"] = "Feu", ["Ice"] = "Glace", ["Lightning"] = "Foudre", ["Wind"] = "Air",
        ["Deprotect"] = "Fragilité", ["Deshell"] = "Défaillance", ["Imperil"] = "Vulnérabilité",
        ["Poison"] = "Poison", ["Dispel"] = "Dissipation", ["Curse"] = "Malédiction",
        ["Pain"] = "Supplice", ["Fog"] = "Oubli", ["Slow"] = "Lenteur", ["Daze"] = "Stupeur",
    };
    private static bool _loaded;

    public static void Load(string path)
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            string key = raw[..tab], value = raw[(tab + 1)..];
            if (key.StartsWith('~'))
                Patterns.Add((new Regex(key[1..], RegexOptions.CultureInvariant), value));
            else
                Fr[key] = value;
        }
    }

    public static string ToFrench(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        if (Fr.TryGetValue(english, out string? fr)) return fr;
        foreach (var (pattern, template) in Patterns)
        {
            Match m = pattern.Match(english);
            if (!m.Success) continue;
            string result = template;
            for (int i = m.Groups.Count - 1; i >= 1; i--)
            {
                string g = m.Groups[i].Value;
                result = result.Replace("{$" + i + "}", Words.TryGetValue(g, out string? w) ? w : g)
                               .Replace("$" + i, g);
            }
            return result;
        }
        return english;
    }
}
