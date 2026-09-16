using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FFXIII2SaveEditor;

public sealed class GameData
{
    public Dictionary<string, string> ItemNames { get; }
    public Dictionary<string, string> MonsterNames { get; }
    public Dictionary<string, MonsterLevelCap> MonsterLevelCaps { get; }
    public List<MonsterLevelCap> MonsterLevelCapCatalog { get; }
    public List<ItemCatalog> ItemCatalog { get; }
    public List<Trait> Traits { get; }
    public Dictionary<string, Trait> TraitsById { get; }
    public Dictionary<string, string> PassiveNames { get; }
    public List<MonsterSkill> MonsterSkills { get; }
    public Dictionary<string, MonsterSkill> MonsterSkillsById { get; }
    public List<AccessoryEffect> AccessoryEffects { get; }
    public Dictionary<string, AccessoryEffect> AccessoryEffectsById { get; }

    private GameData(
        Dictionary<string, string> itemNames,
        Dictionary<string, string> monsterNames,
        List<MonsterLevelCap> monsterLevelCaps,
        List<ItemCatalog> itemCatalog,
        List<Trait> traits,
        List<MonsterSkill> monsterSkills,
        List<AccessoryEffect> accessoryEffects)
    {
        ItemNames = itemNames;
        MonsterNames = monsterNames;
        MonsterLevelCapCatalog = monsterLevelCaps;
        MonsterLevelCaps = monsterLevelCaps.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        ItemCatalog = itemCatalog;
        Traits = traits;
        TraitsById = traits.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        PassiveNames = traits.ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);
        MonsterSkills = monsterSkills;
        MonsterSkillsById = monsterSkills.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        AccessoryEffects = accessoryEffects;
        AccessoryEffectsById = accessoryEffects.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static GameData Load()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Assets");
        string Read(string name)
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Required editor asset is missing: {name}. Expected path: {path}",
                    path);
            return File.ReadAllText(path);
        }

        Dictionary<string, string> itemNames = SaveCore.ParseTsvMap(Read("item_names.tsv"));
        List<ItemCatalog> itemCatalog = SaveCore.ParseItemCatalog(Read("item_catalog.tsv"));
        List<AccessoryEffect> accessoryEffects = SaveCore.ParseAccessoryEffects(Read("character_accessory_effects.tsv"));

        // The curated effect table intentionally contains only entries whose capacity/effect
        // metadata has been verified.  The editor's accessory picker, however, should expose
        // every known acc_* catalog ID.  Add read/write-safe fallback rows for IDs whose
        // gameplay effect/capacity is not yet mapped; the UI marks those rows Unverified and
        // requires explicit confirmation rather than pretending their capacity is known.
        var knownAccessoryIds = accessoryEffects
            .Select(a => a.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ItemCatalog item in itemCatalog.Where(i => i.BagIndex == 2 && i.Id.StartsWith("acc_", StringComparison.OrdinalIgnoreCase)))
        {
            if (knownAccessoryIds.Add(item.Id))
                accessoryEffects.Add(new AccessoryEffect(item.Id, item.Name, "Effect/capacity not yet mapped", "Unmapped", -1));
        }

        return new GameData(
            itemNames,
            SaveCore.ParseTsvMap(Read("monster_names.tsv")),
            SaveCore.ParseMonsterLevelCaps(Read("monster_level_caps.tsv")),
            itemCatalog,
            SaveCore.ParseTraits(Read("monster_traits.tsv")),
            SaveCore.ParseMonsterSkills(Read("monster_skills.tsv")),
            accessoryEffects);
    }
}
