using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FFXIII2SaveEditor;

// Human-readable list of what changed between the save as loaded and the current edited buffer.
// Pure function over two decrypted APP.DAT buffers; shown before any write to disk.
public static class ChangeSummary
{
    public static List<string> Describe(byte[] before, byte[] after, GameData data, bool french)
    {
        var lines = new List<string>();
        string N(uint v) => v.ToString("N0", french ? CultureInfo.GetCultureInfo("fr-FR") : CultureInfo.InvariantCulture);
        void Value(string label, uint a, uint b) { if (a != b) lines.Add($"{label} : {N(a)} → {N(b)}"); }
        uint Read(byte[] buf, int offset, ValueKind kind) => SaveCore.ReadValue(buf, offset, kind);

        // General values
        Value(french ? "Gils" : "Gil", Read(before, SaveCore.GilOffset, ValueKind.BE32), Read(after, SaveCore.GilOffset, ValueKind.BE32));
        Value(french ? "Jetons du casino" : "Casino Coins", Read(before, SaveCore.CasinoCoinsOffset, ValueKind.BE32), Read(after, SaveCore.CasinoCoinsOffset, ValueKind.BE32));
        var casino = new (string En, string Fr, int Offset)[]
        {
            ("Poker games played", "Poker - parties jouées", SaveCore.PokerGamesPlayedOffset),
            ("Poker games won", "Poker - parties gagnées", SaveCore.PokerGamesWonOffset),
            ("Poker matches won", "Poker - matchs gagnés", SaveCore.PokerMatchesWonOffset),
            ("Chronobind games played", "Chronobind - parties jouées", SaveCore.ChronobindGamesPlayedOffset),
            ("Chronobind games won", "Chronobind - parties gagnées", SaveCore.ChronobindGamesWonOffset),
            ("Chronobind matches won", "Chronobind - matchs gagnés", SaveCore.ChronobindMatchesWonOffset),
        };
        foreach (var c in casino)
            Value(french ? c.Fr : c.En, Read(before, c.Offset, ValueKind.BE16), Read(after, c.Offset, ValueKind.BE16));

        // Characters
        var statFr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Equipment Capacity"] = "Capacité d'équipement", ["CP"] = "PC", ["HP"] = "PV", ["ATB"] = "ATB",
            ["Strength"] = "Force", ["Magic"] = "Magie", ["Level"] = "Niveau",
            ["Sentinel"] = "Défenseur", ["Commando"] = "Attaquant", ["Ravager"] = "Ravageur",
            ["Synergist"] = "Tacticien", ["Saboteur"] = "Saboteur", ["Medic"] = "Soigneur",
        };
        string L(string en) => french && statFr.TryGetValue(en, out string? fr) ? fr : en;
        foreach (var (character, fields) in SaveCore.Characters)
            foreach (var (field, spec) in fields)
                Value($"{character} – {L(field)}", Read(before, spec.Offset, spec.Kind), Read(after, spec.Offset, spec.Kind));
        foreach (var (character, fields) in SaveCore.CharacterRoleLevels)
            foreach (var (role, spec) in fields)
                Value($"{character} – {(french ? "niveau " : "")}{L(role)}{(french ? "" : " level")}", Read(before, spec.Offset, spec.Kind), Read(after, spec.Offset, spec.Kind));

        // Fragment skills
        uint fa = SaveCore.FragmentSkillMask(before), fb = SaveCore.FragmentSkillMask(after);
        if (fa != fb)
        {
            int unlocked = SaveCore.FragmentSkills.Count(s => !SaveCore.FragmentSkillUnlocked(fa, s) && SaveCore.FragmentSkillUnlocked(fb, s));
            int cleared = SaveCore.FragmentSkills.Count(s => SaveCore.FragmentSkillUnlocked(fa, s) && !SaveCore.FragmentSkillUnlocked(fb, s));
            lines.Add(french
                ? $"Compétences de fragments : +{unlocked} débloquée(s), -{cleared} effacée(s)"
                : $"Fragment Skills: +{unlocked} unlocked, -{cleared} cleared");
        }

        // Inventory
        try
        {
            var ia = SaveCore.IterInventory(before, data.ItemNames, data.MonsterNames).GroupBy(i => (i.Bag, i.Slot)).ToDictionary(g => g.Key, g => g.First());
            var ib = SaveCore.IterInventory(after, data.ItemNames, data.MonsterNames).GroupBy(i => (i.Bag, i.Slot)).ToDictionary(g => g.Key, g => g.First());
            var itemLines = new List<string>();
            foreach (var key in ia.Keys.Union(ib.Keys))
            {
                ia.TryGetValue(key, out ItemRecord? x);
                ib.TryGetValue(key, out ItemRecord? y);
                if (x is not null && y is not null && x.Id == y.Id && x.Qty == y.Qty) continue;
                if (x is not null && y is not null && x.Id == y.Id)
                    itemLines.Add($"{y.Name} : {x.Qty} → {y.Qty}");
                else if (x is null && y is not null)
                    itemLines.Add($"+ {y.Name} ×{y.Qty}");
                else if (x is not null && y is null)
                    itemLines.Add($"- {x.Name}");
                else if (x is not null && y is not null)
                    itemLines.Add($"{x.Name} → {y.Name}");
            }
            AddGroup(lines, french ? "Objets" : "Items", itemLines, french);
        }
        catch { }

        // Monsters
        var ma = SaveCore.ActiveMonsters(before, data.MonsterNames, data.MonsterLevelCaps).ToDictionary(m => m.Slot);
        var mb = SaveCore.ActiveMonsters(after, data.MonsterNames, data.MonsterLevelCaps).ToDictionary(m => m.Slot);
        string SkillName(string id) => data.MonsterSkillsById.TryGetValue(id, out MonsterSkill? s) ? s.Name : id;
        string TraitName(string id) => data.TraitsById.TryGetValue(id, out Trait? t) ? t.Name : id;
        foreach (int slot in ma.Keys.Union(mb.Keys).OrderBy(s => s))
        {
            ma.TryGetValue(slot, out MonsterRecord? a);
            mb.TryGetValue(slot, out MonsterRecord? b);
            if (a is null || b is null)
            {
                lines.Add(a is null ? $"+ {b!.Name}" : $"- {a.Name}");
                continue;
            }
            var parts = new List<string>();
            void Stat(string en, uint x, uint y) { if (x != y) parts.Add($"{L(en)} {N(x)} → {N(y)}"); }
            Stat("Level", a.Level, b.Level);
            Stat("HP", a.HP, b.HP);
            Stat("Strength", a.Strength, b.Strength);
            Stat("Magic", a.Magic, b.Magic);
            Stat("ATB", a.ATB, b.ATB);

            string[] sa = SaveCore.MonsterAbilityIds(before, slot).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            string[] sb = SaveCore.MonsterAbilityIds(after, slot).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var addedSkills = sb.Except(sa, StringComparer.OrdinalIgnoreCase).Select(SkillName).ToList();
            var removedSkills = sa.Except(sb, StringComparer.OrdinalIgnoreCase).Select(SkillName).ToList();
            if (addedSkills.Count > 0) parts.Add((french ? "aptitudes +" : "skills +") + string.Join(", ", addedSkills));
            if (removedSkills.Count > 0) parts.Add((french ? "aptitudes −" : "skills −") + string.Join(", ", removedSkills));
            if (addedSkills.Count == 0 && removedSkills.Count == 0 && !sa.SequenceEqual(sb, StringComparer.OrdinalIgnoreCase))
                parts.Add(french ? "ordre des aptitudes modifié" : "skill order changed");

            string[] pa = SaveCore.MonsterPassiveIds(before, slot).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            string[] pb = SaveCore.MonsterPassiveIds(after, slot).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var addedPassives = pb.Except(pa, StringComparer.OrdinalIgnoreCase).Select(TraitName).ToList();
            var removedPassives = pa.Except(pb, StringComparer.OrdinalIgnoreCase).Select(TraitName).ToList();
            if (addedPassives.Count > 0) parts.Add((french ? "passifs +" : "passives +") + string.Join(", ", addedPassives));
            if (removedPassives.Count > 0) parts.Add((french ? "passifs −" : "passives −") + string.Join(", ", removedPassives));

            if (parts.Count > 0) lines.Add($"{b.Name} : {string.Join(" ; ", parts)}");
        }

        int bytes = 0;
        int n = Math.Min(before.Length, after.Length);
        for (int i = 0; i < n; i++) if (before[i] != after[i]) bytes++;
        if (lines.Count == 0 && bytes > 0)
            lines.Add(french ? "Modifications non détaillées (autres zones de la sauvegarde)." : "Changes in other save areas (not itemised).");
        if (bytes > 0)
            lines.Add(french ? $"({bytes} octet(s) modifié(s) au total, somme de contrôle comprise)" : $"({bytes} byte(s) changed in total, checksum included)");
        return lines;
    }

    private static void AddGroup(List<string> lines, string title, List<string> items, bool french)
    {
        if (items.Count == 0) return;
        const int max = 15;
        lines.Add($"{title} :");
        lines.AddRange(items.Take(max).Select(i => "   " + i));
        if (items.Count > max) lines.Add(french ? $"   … et {items.Count - max} autre(s)" : $"   … and {items.Count - max} more");
    }
}
