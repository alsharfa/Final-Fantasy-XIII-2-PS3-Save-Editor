using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FFXIII2SaveEditor;

// Role-based ACTIVE skill presets. Add-only: existing skills (and Feral Link) are never removed
// or reordered; recommended same-role abilities that are missing are appended to empty slots in
// priority order until the 32 editable slots are full. Only abilities whose catalogued role
// matches the monster's role are used, so nothing is cross-role. Hidden role abilities and the
// role "Special" abilities (Endless Knightmare / Endless Blessings) are included.
public partial class MainWindow
{
    private static readonly Dictionary<string, string[]> ActivePresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Commando"] = new[]
        {
            "Jeopardize", "Attack", "Blitz", "Area Sweep", "Launch", "Powerchain", "Smite", "Ravage", "Scourge",
            "Blindside", "Bloodthirsty", "Faultsiphon", "Lifesiphon", "Adrenaline", "Armor Breaker",
            "Mind Piercer", "Stagger: Wound", "Stagger: Drain", "Drain Attack",
            "Deprotect Chaser", "Deshell Chaser", "Imperil Chaser", "Poison Chaser", "Slow Chaser",
            "Curse Chaser", "Pain Chaser", "Fog Chaser",
            "Bravery Feeder", "Faith Feeder", "Haste Feeder", "Protect Feeder", "Shell Feeder", "Vigilance Feeder",
        },
        ["Ravager"] = new[]
        {
            "Vigor", "Firaga", "Blizzaga", "Thundaga", "Aeroga", "Ruinga",
            "Fira", "Blizzara", "Thundara", "Aerora",
            "Fire", "Blizzard", "Thunder", "Aero", "Ruin",
            "Flamestrike", "Froststrike", "Sparkstrike", "Galestrike",
            "Ice Blitz", "Electric Blitz", "Aero Blitz",
            "Overwhelm", "Fearsiphon",
        },
        ["Medic"] = new[] { "Curaja", "Curasa", "Cura", "Cure", "Esunada", "Esuna", "Raise", "Cheer" },
        ["Sentinel"] = new[]
        {
            "Reprieve", "Provoke", "Challenge", "Steelguard", "Mediguard", "Element Guard", "Entrench", "Elude",
            "Vendetta", "Deathward", "Fringeward",
        },
        ["Saboteur"] = new[]
        {
            "Endless Knightmare", "Jinx",
            "Heavy Deprotega", "Heavy Deshellga", "Heavy Imperilga", "Heavy Poisonga", "Heavy Painga",
            "Heavy Fogga", "Heavy Cursega",
            "Deprotega", "Deshellga", "Imperilga", "Poisonga", "Painga", "Fogga", "Cursega", "Woundga", "Dispelga",
            "Deprotect II", "Deshell II", "Imperil II", "Poison II", "Pain II", "Fog II", "Curse II",
            "Deprotect", "Deshell", "Imperil", "Poison", "Pain", "Fog", "Curse", "Wound", "Dispel",
        },
        ["Synergist"] = new[]
        {
            "Endless Blessings", "Boon",
            "Bravega", "Faithga", "Protectga", "Shellga", "Veilga", "Vigilaga", "Haste",
            "Bravery", "Faith", "Protect", "Shell", "Veil", "Vigilance",
            "Enfire", "Enfrost", "Enthunder", "Enaero",
        },
    };

    private void ApplyActivePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (_selectedMonster is null) { Warn("Select a target monster first."); return; }
        if (_abilityCountInfo is null)
        {
            Warn("Active-skill add/remove is locked because the ability-count field could not be proven from this save.");
            return;
        }

        string role = InferMonsterRole(_selectedMonster.Slot);
        if (string.IsNullOrEmpty(role) || !ActivePresets.TryGetValue(role, out string[]? names))
        {
            Warn("The monster's role could not be detected, so no active preset can be chosen.");
            return;
        }

        string[] ids = SaveCore.MonsterAbilityIds(Plain, _selectedMonster.Slot);
        var present = new HashSet<string>(ids.Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);
        int free = ids.Skip(SaveCore.MonsterFirstSkillSlot).Count(string.IsNullOrEmpty);

        var byName = _data.MonsterSkills
            .Where(s => string.Equals(s.Role, role, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        List<MonsterSkill> missing = names
            .Select(n => byName.GetValueOrDefault(n))
            .Where(s => s is not null && !present.Contains(s.Id) && !IsFeralLink(s.Id))
            .Select(s => s!)
            .ToList();

        if (missing.Count == 0)
        {
            Status($"{_selectedMonster.Name} already has every recommended {role} active skill.");
            return;
        }
        if (free == 0)
        {
            Warn("All 32 active skill slots are already used. Remove some skills first.");
            return;
        }

        List<MonsterSkill> toAdd = missing.Take(free).ToList();
        int dropped = missing.Count - toAdd.Count;
        string list = string.Join(", ", toAdd.Select(s => s.Name));
        string note = dropped > 0 ? $"\n\n{dropped} lower-priority skill(s) do not fit and are skipped." : "";
        if (!Confirm($"Add {toAdd.Count} recommended {role} active skill(s) to {_selectedMonster.Name}?\n\n{list}{note}\n\nExisting skills are kept. You can use Undo Monster Edit afterward."))
            return;

        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            foreach (MonsterSkill skill in toAdd)
                SaveCore.AddMonsterAbility(work, _selectedMonster.Slot, skill.Id, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"{role} active preset applied: {toAdd.Count} skill(s) added");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Active preset failed"); }
    }
}
