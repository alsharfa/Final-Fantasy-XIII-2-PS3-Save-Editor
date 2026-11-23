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
            "Mind Piercer", "Deathblow", "Stagger: Wound", "Stagger: Drain", "Drain Attack",
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
            "Heat Blitz", "Ice Blitz", "Electric Blitz", "Aero Blitz",
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
            "Heavy Fogga", "Heavy Cursega", "Heavy Dispelga",
            "Deprotega", "Deshellga", "Imperilga", "Poisonga", "Painga", "Fogga", "Cursega", "Woundga", "Dispelga",
            "Deprotect II", "Deshell II", "Imperil II", "Poison II", "Pain II", "Fog II", "Curse II", "Dispel II",
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

    // Missing recommended same-role active skills, in priority order (not truncated to free slots).
    private List<MonsterSkill> MissingActivePresetSkills(byte[] buffer, int slot, string role)
    {
        if (!ActivePresets.TryGetValue(role, out string[]? names)) return new List<MonsterSkill>();
        string[] ids = SaveCore.MonsterAbilityIds(buffer, slot);
        var present = new HashSet<string>(ids.Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);
        var byName = _data.MonsterSkills
            .Where(s => string.Equals(s.Role, role, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return names
            .Select(n => byName.GetValueOrDefault(n))
            .Where(s => s is not null && !present.Contains(s.Id) && !IsFeralLink(s.Id))
            .Select(s => s!)
            .ToList();
    }

    private static int FreeActiveSlots(byte[] buffer, int slot)
        => SaveCore.MonsterAbilityIds(buffer, slot).Skip(SaveCore.MonsterFirstSkillSlot).Count(string.IsNullOrEmpty);

    private static string PassivePresetKeyForRole(string role) => role switch
    {
        "Commando" => "COM",
        "Ravager" => "RAV",
        "Medic" => "MED",
        "Sentinel" => "TANK",
        _ => "BALANCED"
    };

    private static string PassivePresetName(string key) => key switch
    {
        "COM" => "Best COM",
        "RAV" => "Best RAV",
        "MED" => "Best MED",
        "TANK" => "Tank",
        _ => "Balanced"
    };

    // One click: role-based passive preset + role-based active preset, one confirmation, one undo step.
    // Stats are deliberately untouched.
    private void OptimizeMonster_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (_selectedMonster is null) { Warn("Select a target monster first."); return; }
        string role = InferMonsterRole(_selectedMonster.Slot);
        if (string.IsNullOrEmpty(role)) { Warn("The monster's role could not be detected, so no active preset can be chosen."); return; }

        string key = PassivePresetKeyForRole(role);
        List<Trait> desired = PassivePresets.TryGetValue(key, out string[]? presetIds)
            ? presetIds.Select(id => _data.TraitsById.GetValueOrDefault(id)).Where(t => t is not null && !t.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase)).Select(t => t!).ToList()
            : new List<Trait>();

        List<MonsterSkill> missing = _abilityCountInfo is null ? new List<MonsterSkill>() : MissingActivePresetSkills(Plain, _selectedMonster.Slot, role);
        int free = FreeActiveSlots(Plain, _selectedMonster.Slot);
        List<MonsterSkill> toAdd = missing.Take(free).ToList();

        string passiveList = desired.Count == 0 ? "  -" : string.Join("\n", desired.Select(t => "  • " + t.Name));
        string activeList = _abilityCountInfo is null
            ? "  (locked: ability-count field not verified)"
            : toAdd.Count == 0 ? "  -" : string.Join(", ", toAdd.Select(s => s.Name));
        if (!Confirm($"Optimize {_selectedMonster.Name} ({role})?\n\nPassives ({PassivePresetName(key)}):\n{passiveList}\n\nActive skills to add:\n{activeList}\n\nStats are not changed. Existing skills are kept and awp_* passives are preserved. One Undo Monster Edit reverts everything."))
            return;

        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            var (changed, added, _, _) = desired.Count > 0 ? ApplyPassivePresetTo(work, _selectedMonster.Slot, desired) : (0, 0, 0, 0);
            foreach (MonsterSkill skill in toAdd)
                SaveCore.AddMonsterAbility(work, _selectedMonster.Slot, skill.Id, _abilityCountInfo);
            if (added > 0 || toAdd.Count > 0)
                SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            if (changed == 0 && toAdd.Count == 0)
            {
                Status($"{_selectedMonster.Name} is already optimized for {role}.");
                return;
            }
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"{_selectedMonster.Name} optimized: {changed} passive(s), {toAdd.Count} active skill(s)");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Optimize failed"); }
    }

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
