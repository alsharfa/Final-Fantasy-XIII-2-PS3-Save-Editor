using System;
using System.Collections.Generic;

namespace FFXIII2SaveEditor;

public enum ValueKind
{
    U8,
    BE16,
    BE32
}

public readonly record struct FieldSpec(int Offset, ValueKind Kind, uint Max);
public readonly record struct FieldWrite(int Offset, ValueKind Kind, uint Value, uint Max);
public readonly record struct BagSpec(string Name, int Base, int Capacity, bool NormalIds);
public readonly record struct MonsterFieldSpec(int RelativeOffset, ValueKind Kind, uint Max);
public sealed record FragmentSkill(string Name, uint[] Bits, string Description);
public sealed record ItemCatalog(string Id, string Name, string Category, int BagIndex, int MaxQty, bool Editable);
public sealed record Trait(string Id, string Name, string Family, string Category, string Value);
public sealed record MonsterSkill(string Id, string Name, string Role, string Category);
public sealed record ChocoboRaceAbility(string Name, string Effect, string[] PassiveIds);
public sealed record MonsterLevelCap(string Id, string Name, string Role, uint MaxLevel);
public sealed class MonsterLevelReferenceViewRow
{
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public uint MaxLevel { get; init; }
    public string Mapping { get; init; } = "";
    public string Id { get; init; } = "";
}
public sealed record AccessoryEffect(string Id, string Name, string Effect, string Category, int Capacity);
public sealed record PfdEntry(string Name, byte[] Key, ulong Size);

public sealed class MonsterRecord
{
    public int Slot { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public uint Level { get; init; }
    public uint MaxLevel { get; init; }
    public string LevelDisplay => MaxLevel > 0 ? $"{Level} / {MaxLevel}" : Level.ToString();
    public bool LevelOverCap => MaxLevel > 0 && Level > MaxLevel;
    public uint HP { get; init; }
    public uint Strength { get; init; }
    public uint Magic { get; init; }
    public uint ATB { get; init; }
}

public sealed class ItemRecord
{
    public int BagIndex { get; init; }
    public int Slot { get; init; }
    public int QtyOffset { get; init; }
    public string Bag { get; init; } = "";
    public string Id { get; init; } = "";
    public string ResolvedId { get; init; } = "";
    public string Name { get; init; } = "";
    public int Qty { get; init; }
    public bool Editable { get; init; }
}

public sealed class EquipmentRecord
{
    public int BagIndex { get; init; }
    public int Slot { get; init; }
    public string Category { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public uint Flags { get; init; }
}

public enum RegionSupport
{
    Unknown,
    SupportedUnverified,
    Tested
}

public sealed class SaveLoadResult
{
    public string Folder { get; init; } = "";
    public byte[] Plain { get; init; } = Array.Empty<byte>();
    public ulong WorkingKey { get; init; }
    public string Mode { get; init; } = "";
    public string TitleId { get; init; } = "";
    public bool PfdProtected { get; init; }
    public bool PfdMetadataReliable { get; init; } = true;
    public IReadOnlyList<string> PfdNames { get; init; } = Array.Empty<string>();
    public int FragmentCount { get; init; } = -1;
}

public sealed class InventoryViewRow
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public int Quantity { get; init; }
    public bool Editable { get; init; }
    public ItemCatalog? Catalog { get; init; }
    public ItemRecord? Record { get; init; }
    public string EditableText => Editable ? "Yes" : "No";
}

public sealed class AdornmentViewRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Owned { get; set; }
    public ItemCatalog? Catalog { get; init; }
    public string Ownership => Owned ? "Owned" : "Not Owned";
}

public sealed class EquipmentViewRow
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public int Slot { get; init; }
    public string State { get; init; } = "Owned";
    public EquipmentRecord? Record { get; init; }
}

public sealed class EquipmentCatalogViewRow
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Ownership { get; init; } = "";
    public ItemCatalog? Catalog { get; init; }
}


public sealed class CharacterAccessoryViewRow
{
    public int EquipPosition { get; init; }
    public int ManagerSlot { get; init; }
    public string Name { get; init; } = "";
    public string Effect { get; init; } = "";
    public int Capacity { get; init; }
    public string CapacityText => Capacity >= 0 ? Capacity.ToString() : "?";
    public AccessoryEffect? EffectInfo { get; init; }
}

public sealed class CharacterAccessoryCatalogViewRow
{
    public string Name { get; init; } = "";
    public string Effect { get; init; } = "";
    public string Category { get; init; } = "";
    public int Capacity { get; init; }
    public string CapacityText => Capacity >= 0 ? Capacity.ToString() : "?";
    public string Safety { get; init; } = "";
    public AccessoryEffect? EffectInfo { get; init; }
}

public sealed class TraitViewRow
{
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public Trait? Trait { get; init; }
    public string Category => Trait?.Category ?? (Name == "Empty" ? "Empty" : "Unmapped");
    public string Value => Trait?.Value ?? "";
    public string Description => Trait is null
        ? (Name == "Empty" ? "This passive slot is empty." : "This passive ID is not mapped in the current catalog.")
        : SaveCore.DescribeTrait(Trait);
    public bool IsAdvanced => Trait?.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase) == true;
}


public sealed class ChocoboRaceAbilityViewRow
{
    public string Status { get; init; } = "Available";
    public string Name { get; init; } = "";
    public string SourcePassive { get; init; } = "";
    public string Effect { get; init; } = "";
    public bool IsActive { get; init; }
    public ChocoboRaceAbility? Ability { get; init; }
}

public sealed class MonsterSkillViewRow
{
    public int Slot { get; init; }
    public int StorageIndex { get; init; } = -1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Empty => string.IsNullOrEmpty(Id);
    public MonsterSkill? Skill { get; init; }
}

public sealed class MonsterAbilityCountInfo
{
    public int RelativeOffset { get; init; }
    public ValueKind Kind { get; init; }
    public bool CountsTotalAbilities { get; init; }
    public int Matches { get; init; }
    public int Samples { get; init; }
    public double Confidence => Samples == 0 ? 0 : (double)Matches / Samples;
    public string Description => RelativeOffset == SaveCore.MonsterActiveCountRelative && CountsTotalAbilities
        ? $"active@0x{SaveCore.MonsterActiveCountRelative:X3} + passive@0x{SaveCore.MonsterPassiveCountRelative:X3} / U8 / {Matches}/{Samples}"
        : $"0x{RelativeOffset:X3} / {Kind} / {(CountsTotalAbilities ? "active+passive" : "active")} / {Matches}/{Samples}";
}

public sealed class InfusionTransferItem
{
    public bool Include { get; set; } = true;
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Role { get; init; } = "";
    public string Note { get; init; } = "";
    public MonsterSkill? Skill { get; init; }
    public Trait? Trait { get; init; }
}

public sealed class FragmentCollectionViewRow
{
    public string Name { get; init; } = "";
}


public sealed class FragmentSkillViewRow
{
    public string Name { get; init; } = "";
    public string Unlocked { get; init; } = "No";
    public string Effect { get; init; } = "";
    public FragmentSkill? Skill { get; init; }
}



public sealed class GuestPartyOption
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string Note { get; init; } = "";
    public bool CanWrite { get; init; }
}
