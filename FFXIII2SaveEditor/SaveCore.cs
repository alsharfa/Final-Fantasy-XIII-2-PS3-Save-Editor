using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FFXIII2SaveEditor;

public static class SaveCore
{
    public const ulong FFXIII2Key = 0x9B1F01011A6438B0UL;
    public const uint FFXIIIConst = 0xA1652347U;
    public const int GilOffset = 0x2316;
    public const int CasinoCoinsOffset = 0x10CBA;
    // Serendipity table-game records. Controlled captures verified Poker Games Played
    // at 0x15CE8 (1 -> 3) and Chronobind Games Played at 0x15CF8 (0 -> 1).
    // The Won / Matches Won fields occupy the adjacent BE16 entries in the same record layout.
    public const int PokerGamesPlayedOffset = 0x15CE8;
    public const int PokerGamesWonOffset = 0x15CEC;
    public const int PokerMatchesWonOffset = 0x15CF0;
    public const int ChronobindGamesPlayedOffset = 0x15CF8;
    public const int ChronobindGamesWonOffset = 0x15CFC;
    public const int ChronobindMatchesWonOffset = 0x15D00;
    public const uint CasinoRecordMax = 65535;
    public const int FragmentSkillsOffset = 0x17A56;
    public const int FragmentCount = 160;
    public const int MonsterBase = 0x6368C;
    public const int MonsterStride = 0x2A6;
    public const int MonsterSlots = 226;
    public const int MonsterIdDelta = 0x27C;
    // Raw monster records contain 33 x 14-byte entries beginning at record+0x1C.
    // Real APP.DAT captures show entry 0 is reserved/internal metadata (e.g. Cait Sith can
    // contain e318 there) and is NOT included in the game's active-skill counter.
    // User-editable active skills therefore occupy raw entries 1..32 (32 slots total).
    public const int MonsterAbilityCount = 33;
    public const int MonsterFirstSkillSlot = 1;
    public const int MonsterEditableAbilityCount = MonsterAbilityCount - MonsterFirstSkillSlot;
    public const int MonsterAbilitySize = 0x0E;
    public const int MonsterAbilityDelta = 0x260;
    public const int MonsterPassiveCount = 10;
    public const int MonsterActiveCountRelative = 0x286;
    public const int MonsterPassiveCountRelative = 0x287;
    public const int MonsterRoleRelative = 0x288;
    public const int MonsterPassiveSize = 0x0E;
    public const int MonsterPassiveDelta = 0x92;

    // Chocobo racing Speed/Stamina continue to use the tamed monster's battle
    // Strength/Magic values. RP is NOT derived from HP. A real BLES01269 save
    // paired with the in-game Golden Chocobo profile verified RP 25 at APP.DAT
    // +0x10CDC as a big-endian UInt16. The same profile showed Coins Won 8200
    // at +0x10CE4, placing both values in the same racing-profile block.
    public const uint ChocoboRaceStatCap = 800;
    public const int ChocoboRaceRpOffset = 0x10CDC;
    public const uint ChocoboRaceRpCap = 600;
    public const int EquipRecordSize = 0x12;
    public const int AccessoryManagerBase = 0x5C650;
    public const int WeaponManagerBase = 0x5DBF8;
    public const int AccessoryManagerSlots = 300;
    public const int CharacterEquipmentReferenceCount = 8;
    public const int CharacterAccessoryReferenceStart = 1;
    // FFXIII-2 exposes four accessory slots per Serah/Noel. The raw equipment-reference
    // array is larger, but entries after weapon + four accessories are reserved/unknown and
    // must not be interpreted as equipable accessory slots.
    public const int CharacterAccessorySlotCount = 4;
    public const int CharacterAccessoryCapacityMax = 255;
    public const uint EquippedFlag = 0x01000000U;
    // EquipMan is a single indexed table: accessories begin at index 0 and weapons begin at
    // (WeaponManagerBase-AccessoryManagerBase)/0x12 = 308. Character equipment references
    // point into that combined table.
    public const int WeaponManagerIndexBase = 308;
    public static readonly Dictionary<string, int> CharacterEquipmentReferenceBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Serah"] = 0x36D98,
        ["Noel"] = 0x343F8,
    };

    private static readonly byte[] AppMarker = Encoding.ASCII.GetBytes("db_partyman");

    public static readonly Dictionary<string, Dictionary<string, FieldSpec>> Characters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Serah"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Equipment Capacity"] = new(0x36C92, ValueKind.U8, CharacterAccessoryCapacityMax),
            ["CP"] = new(0x36C94, ValueKind.BE32, 99_999_999),
            ["HP"] = new(0x36C9C, ValueKind.BE32, 99_999),
            ["ATB"] = new(0x36CA7, ValueKind.U8, 60),
            ["Strength"] = new(0x36CAC, ValueKind.BE32, 9_999),
            ["Magic"] = new(0x36CB0, ValueKind.BE32, 9_999),
        },
        ["Noel"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Equipment Capacity"] = new(0x342F2, ValueKind.U8, CharacterAccessoryCapacityMax),
            ["CP"] = new(0x342F4, ValueKind.BE32, 99_999_999),
            ["HP"] = new(0x342FC, ValueKind.BE32, 99_999),
            ["ATB"] = new(0x34307, ValueKind.U8, 60),
            ["Strength"] = new(0x3430C, ValueKind.BE32, 9_999),
            ["Magic"] = new(0x34310, ValueKind.BE32, 9_999),
        },
        ["Lightning DLC"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CP"] = new(0x31956, ValueKind.BE16, 10_000),
            ["HP"] = new(0x3195C, ValueKind.BE32, 999_999),
            ["ATB"] = new(0x31967, ValueKind.U8, 60),
            ["Strength"] = new(0x3196E, ValueKind.BE16, 9_999),
            ["Magic"] = new(0x31972, ValueKind.BE16, 9_999),
        },
    };


    // Serah/Noel role levels are six consecutive U8 values in the character block.
    // Verified against multiple real BLES01269 saves at different progression states:
    // Noel  0x342E8..0x342ED
    // Serah 0x36C88..0x36C8D
    // Internal role order follows the game's role-code ordering:
    // SEN, COM, RAV, SYN, SAB, MED.
    public const uint CharacterRoleLevelMax = 99;

    public static readonly string[] CharacterRoleOrder =
    {
        "Sentinel", "Commando", "Ravager", "Synergist", "Saboteur", "Medic"
    };

    public static readonly Dictionary<string, Dictionary<string, FieldSpec>> CharacterRoleLevels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Serah"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Sentinel"] = new(0x36C88, ValueKind.U8, CharacterRoleLevelMax),
                ["Commando"] = new(0x36C89, ValueKind.U8, CharacterRoleLevelMax),
                ["Ravager"] = new(0x36C8A, ValueKind.U8, CharacterRoleLevelMax),
                ["Synergist"] = new(0x36C8B, ValueKind.U8, CharacterRoleLevelMax),
                ["Saboteur"] = new(0x36C8C, ValueKind.U8, CharacterRoleLevelMax),
                ["Medic"] = new(0x36C8D, ValueKind.U8, CharacterRoleLevelMax),
            },
            ["Noel"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Sentinel"] = new(0x342E8, ValueKind.U8, CharacterRoleLevelMax),
                ["Commando"] = new(0x342E9, ValueKind.U8, CharacterRoleLevelMax),
                ["Ravager"] = new(0x342EA, ValueKind.U8, CharacterRoleLevelMax),
                ["Synergist"] = new(0x342EB, ValueKind.U8, CharacterRoleLevelMax),
                ["Saboteur"] = new(0x342EC, ValueKind.U8, CharacterRoleLevelMax),
                ["Medic"] = new(0x342ED, ValueKind.U8, CharacterRoleLevelMax),
            },
        };

    public static readonly BagSpec[] InventoryBags =
    {
        new("Consumables", 0x4A264, 50, true),
        new("Weapons", 0x4A8A8, 100, false),
        new("Accessories", 0x4B52C, 300, false),
        new("Components / Materials", 0x4DAB0, 200, true),
        new("Key Items / OOPArts", 0x4F3B4, 130, true),
        new("Monster Crystals", 0x503F8, 200, false),
        new("Internal / Special", 0x51CFC, 400, true),
        new("Casino / Specialty", 0x54F00, 32, true),
        new("Monster Growth Materials", 0x55304, 40, true),
    };

    public static readonly Dictionary<string, MonsterFieldSpec> MonsterFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Level"] = new(0x00, ValueKind.U8, 99),
        ["HP"] = new(0x0E, ValueKind.BE32, 999_999),
        ["Strength"] = new(0x12, ValueKind.BE32, 99_999),
        ["Magic"] = new(0x16, ValueKind.BE32, 99_999),
        ["ATB"] = new(0x1D, ValueKind.U8, 60),
    };

    public static bool IsChocoboMonster(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("Chocobo", StringComparison.OrdinalIgnoreCase)) return true;

        // Known tameable chocobo monster IDs in the bundled name/catalog data.
        // Keep the name check above so alternate regional/internal aliases still work.
        return id.Equals("k865", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k866", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k867", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k868", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k869", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k870", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k871", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k872", StringComparison.OrdinalIgnoreCase)
            || id.Equals("k882", StringComparison.OrdinalIgnoreCase);
    }

    public static string ChocoboRaceGrade(uint stat)
    {
        uint capped = Math.Min(stat, ChocoboRaceStatCap);
        if (capped <= 160) return "E";
        if (capped <= 320) return "D";
        if (capped <= 480) return "C";
        if (capped <= 640) return "B";
        return "A";
    }

    // Chocobo race abilities are derived from passive abilities on the tamed monster.
    // The IDs below are only IDs already mapped by this editor; multiple IDs can grant
    // the same race ability. The first ID is the preferred direct-save source used by
    // the Chocobo Racing tab when the user clicks Add Selected Ability.
    public static readonly ChocoboRaceAbility[] ChocoboRaceAbilities =
    {
        new("Rocket Blast", "Improves the timing window for a Sprinting Start.", new[] { "auto_rol", "auto_syncmax", "auto_p_shel" }),
        new("Dark Horse", "Improves betting odds when the chocobo is underestimated.", new[] { "auto_drop_0", "auto_drop_1" }),
        new("Limelight", "Raises racing stats in graded races.", new[] { "auto_b_atb", "auto_b_libra" }),
        new("Supersonic", "Strategy 1: increases Boost gauge charge rate.", new[] { "awp_c001_000", "awp_c001_100", "awp_c001_050", "awp_c001_150" }),
        new("Lightning Bolt", "Strategy 2: increases Boost gauge charge rate.", new[] { "awp_c002_200", "awp_c002_100", "awp_c001_200", "awp_c002_250", "awp_c002_150", "awp_c001_250" }),
        new("Turbo", "Strategy 3: reduces the cost of Boost.", new[] { "awp_c003_000", "awp_c004_000", "awp_c003_200", "awp_c003_050", "awp_c004_050", "awp_c003_250" }),
        new("Blue Streak", "Strategy 4: reduces the cost of Boost.", new[] { "auto_p_prot", "awp_c004_100", "awp_c004_200", "awp_c004_150", "awp_c004_250" }),
        new("Health Nut", "Keeps the chocobo's race condition favorable.", new[] { "auto_shorten" }),
        new("Free Spirit", "Helps the chocobo maintain its own pace despite the pack.", new[] { "auto_forc", "auto_brav", "auto_fait", "auto_hast", "auto_myte", "auto_p_fait" }),
        new("Attention Hog", "Raises stats when the chocobo is the favorite.", new[] { "auto_syncup", "auto_gilup" }),
        new("Runaway", "Strengthens Supersonic and Lightning Bolt.", new[] { "auto_triplets", "auto_law", "awp_all_250" }),
        new("Second Wind", "Strengthens Turbo and Blue Streak.", new[] { "awp_c005_200", "awp_all_350", "awp_c005_250" }),
        new("Perseverance", "Restores some Boost if stamina runs out near the finish.", new[] { "auto_prot", "auto_shel", "auto_veil", "awp_c000_000", "awp_c000_100", "awp_c000_050", "awp_c000_150" }),
        new("Sprinter", "Greatly improves speed in short-distance races.", new[] { "auto_00_0", "auto_p_brav", "auto_p_veil", "auto_00_1" }),
        new("Marathoner", "Greatly improves stamina in long-distance races.", new[] { "auto_p_forc", "auto_p_myte" }),
    };

    // Performance-first universal racing preset. The ten passive slots are spent on
    // two complementary strategy packages (Strategy 4 + Strategy 1), both distance
    // bonuses, and the strongest broadly useful condition/stat helpers. Betting-only
    // Dark Horse, redundant Perseverance, start-timing Rocket Blast, and the two extra
    // strategy skills are intentionally excluded.
    public static readonly string[] GodChocoboRaceAbilityNames =
    {
        "Blue Streak",
        "Second Wind",
        "Supersonic",
        "Runaway",
        "Sprinter",
        "Marathoner",
        "Limelight",
        "Health Nut",
        "Free Spirit",
        "Attention Hog",
    };

    public static bool ChocoboRaceAbilityActive(IEnumerable<string> passiveIds, ChocoboRaceAbility ability)
    {
        var current = passiveIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ability.PassiveIds.Any(current.Contains);
    }

    public static readonly FragmentSkill[] FragmentSkills =
    {
        new("Mog's Manifestation", new uint[] { 0 }, "Let Mog find treasure where no treasure can be seen."),
        new("Bargain Hunter", new uint[] { 1 }, "Buy items from shops for less gil."),
        new("Haggler", new uint[] { 2 }, "Sell items to shops for more gil."),
        new("Chocobo Music", new uint[] { 3, 16, 17, 18 }, "Unlock the selectable chocobo music choices."),
        new("Anti-grav Jump", new uint[] { 4 }, "Defy gravity and jump further."),
        new("Paradox Scope", new uint[] { 5 }, "Invoke a paradox to explore new potential histories."),
        new("Rolling in CP", new uint[] { 6 }, "Earn more CP from battles."),
        new("Mobile Mog", new uint[] { 7 }, "Mog returns quickly to your side after throwing him."),
        new("Monster Collector", new uint[] { 8 }, "Increase the rate of monster crystals obtained from battles."),
        new("Encounter Master", new uint[] { 9, 10 }, "Unlock both higher and lower enemy encounter-rate choices."),
        new("Battlemania", new uint[] { 11 }, "Increase the encounter rate for powerful enemies."),
        new("Field Killer", new uint[] { 12 }, "Defeat weaker enemies directly in the field."),
        new("Clock Master", new uint[] { 13, 14 }, "Unlock both high-speed and low-speed game options."),
        new("Eyes of the Goddess", new uint[] { 15 }, "Control the camera during event scenes."),
    };

    // Verified against the user's real BLES01269 save with all Fragment Skills unlocked:
    // decrypted APP.DAT + 0x17A56 = 00 07 FF FF (BE32 0x0007FFFF).
    // These 19 known bits represent the game's 14 Fragment Skills.
    public const uint FragmentSkillsAllMask = 0x0007FFFF;

    private static readonly byte[] SysconManagerKey =
    {
        0xD4, 0x13, 0xB8, 0x96, 0x63, 0xE1, 0xFE, 0x9F,
        0x75, 0x14, 0x3D, 0x3B, 0xB4, 0x56, 0x52, 0x74
    };

    private static readonly Dictionary<string, byte[]> SecureFileIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BLES01269"] = new byte[] { 0x58, 0xD0, 0xAB, 0xA0, 0x0D, 0x12, 0x7D, 0x50, 0xB9, 0x25, 0x63, 0x4D, 0xF5, 0x0E, 0x63, 0xE9 },
        ["BLUS30776"] = new byte[] { 0x20, 0xCC, 0xC7, 0x9F, 0x1C, 0x3B, 0x8B, 0x24, 0x91, 0x47, 0x53, 0x51, 0xC4, 0x86, 0xB4, 0x47 },
        ["BCAS20224"] = new byte[] { 0xE7, 0x78, 0x35, 0x98, 0x21, 0x1C, 0xF6, 0x34, 0x24, 0xB9, 0x4E, 0xE9, 0x4E, 0x4A, 0x5D, 0x2C },
    };

    public static ulong DeriveWorkingKey(byte[] key)
    {
        if (key.Length != 16)
            throw new InvalidDataException($"KEY.DAT must be exactly 16 bytes (got {key.Length})");
        ulong a = BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(0, 8));
        ulong b = BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(8, 8));
        return FFXIII2Key ^ ((a ^ b) | 1UL);
    }

    public static byte[] InitKeyTable(ulong k)
    {
        unchecked
        {
            k = ((k & 0xFF00000000000000UL) >> 16) | ((k & 0x0000FF0000000000UL) << 16) | (k & 0x00FF00FFFFFFFFFFUL);
            k = ((k & 0x00000000FF00FF00UL) >> 8) | ((k & 0x0000000000FF00FFUL) << 8) | (k & 0xFFFFFFFF00000000UL);
            var table = new byte[256];
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(0, 8), k);
            table[0] = (byte)(table[0] + 0x45);
            for (int j = 1; j < 8; j++)
            {
                ushort init0 = (ushort)(table[j - 1] + table[j]);
                ushort init1 = (ushort)((table[j - 1] << 2) & 0x3FC);
                table[j] = (byte)((((init0 + 0xD4) ^ init1) & 0xFF) ^ 0x45);
            }
            k = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(0, 8));
            for (int j = 1; j < 32; j++)
            {
                k += (k << 2) & 0xFFFFFFFFFFFFFFFCUL;
                BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(j * 8, 8), k);
            }
            return table;
        }
    }

    public static byte[] DecryptBytes(byte[] data, ulong ffKey)
    {
        if ((data.Length & 7) != 0)
            throw new InvalidDataException("APP.DAT size must be a multiple of 8 bytes");

        byte[] mem = (byte[])data.Clone();
        byte[] kt = InitKeyTable(ffKey);
        int byteCounter = 0;
        uint blockCounter = 0;
        int keyCtr = 0;

        unchecked
        {
            while (byteCounter < mem.Length)
            {
                if (keyCtr > 31) keyCtr = 0;
                ulong cog64 = (ulong)byteCounter << 0x14;
                uint cogLo = (uint)cog64;
                uint cogHi = (uint)(cog64 >> 32);
                uint gear1 = cogLo | (uint)(byteCounter << 10) | (uint)byteCounter;
                uint carry1 = gear1 > ~FFXIIIConst ? 1U : 0U;
                gear1 += FFXIIIConst;
                uint gear2 = (blockCounter * 2U | cogHi) + carry1;
                int baseOffset = byteCounter;
                byte old = 0;

                for (int b = 0; b < 8; b++)
                {
                    int pos = baseOffset + b;
                    if (b == 0)
                    {
                        old = mem[pos];
                        mem[pos] = (byte)(0x45 ^ (byte)blockCounter ^ mem[pos]);
                    }
                    else
                    {
                        byte iv = (byte)(mem[pos] ^ old);
                        old = mem[pos];
                        mem[pos] = iv;
                    }
                    for (int i = 0; i < 8; i++)
                        mem[pos] = (byte)(0x78 + mem[pos] - kt[keyCtr * 8 + i]);
                }

                uint ta = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(baseOffset, 4));
                uint tb = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(baseOffset + 4, 4));
                uint ka = BinaryPrimitives.ReadUInt32LittleEndian(kt.AsSpan(keyCtr * 8, 4));
                uint kb = BinaryPrimitives.ReadUInt32LittleEndian(kt.AsSpan(keyCtr * 8 + 4, 4));
                uint carry2 = ta < ka ? 1U : 0U;
                ta = ka ^ gear1 ^ (ta - ka);
                tb = kb ^ gear2 ^ (tb - kb - carry2);
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(baseOffset, 4), tb);
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(baseOffset + 4, 4), ta);

                byteCounter += 8;
                blockCounter++;
                keyCtr++;
            }
        }
        return mem;
    }

    public static byte[] EncryptBytes(byte[] data, ulong ffKey)
    {
        if ((data.Length & 7) != 0)
            throw new InvalidDataException("APP.DAT size must be a multiple of 8 bytes");

        byte[] mem = (byte[])data.Clone();
        byte[] kt = InitKeyTable(ffKey);
        int byteCounter = 0;
        uint blockCounter = 0;
        int keyCtr = 0;

        unchecked
        {
            while (byteCounter < mem.Length)
            {
                if (keyCtr > 31) keyCtr = 0;
                ulong cog64 = (ulong)byteCounter << 0x14;
                uint cogLo = (uint)cog64;
                uint cogHi = (uint)(cog64 >> 32);
                uint gear1 = cogLo | (uint)(byteCounter << 10) | (uint)byteCounter;
                uint carry1 = gear1 > ~FFXIIIConst ? 1U : 0U;
                gear1 += FFXIIIConst;
                uint gear2 = (blockCounter * 2U | cogHi) + carry1;

                uint ka = BinaryPrimitives.ReadUInt32LittleEndian(kt.AsSpan(keyCtr * 8, 4));
                uint kb = BinaryPrimitives.ReadUInt32LittleEndian(kt.AsSpan(keyCtr * 8 + 4, 4));
                uint first = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(byteCounter, 4));
                uint second = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(byteCounter + 4, 4));
                uint tb = kb ^ gear2 ^ first;
                uint ta = ka ^ gear1 ^ second;
                uint carry2 = ta > ~ka ? 1U : 0U;
                tb += kb + carry2;
                ta += ka;
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(byteCounter, 4), ta);
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(byteCounter + 4, 4), tb);

                byte old = 0;
                for (int b = 0; b < 8; b++)
                {
                    int pos = byteCounter + b;
                    for (int i = 7; i >= 0; i--)
                        mem[pos] = (byte)(mem[pos] + kt[keyCtr * 8 + i] - 0x78);
                    if (b == 0)
                    {
                        mem[pos] = (byte)(0x45 ^ (byte)blockCounter ^ mem[pos]);
                        old = mem[pos];
                    }
                    else
                    {
                        mem[pos] ^= old;
                        old = mem[pos];
                    }
                }

                byteCounter += 8;
                blockCounter++;
                keyCtr++;
            }
        }
        return mem;
    }

    public static uint CalculateChecksum(byte[] plain)
    {
        unchecked
        {
            uint sum = 0;
            if (plain.Length < 8) return 0;
            for (int i = 0; i < plain.Length - 8; i += 4)
                sum += plain[i];
            return sum;
        }
    }

    public static uint StoredChecksum(byte[] plain)
        => plain.Length < 4 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(plain.Length - 4, 4));

    public static byte[] RepairChecksum(byte[] plain)
    {
        byte[] output = (byte[])plain.Clone();
        if (output.Length >= 4)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(output.Length - 4, 4), CalculateChecksum(output));
        return output;
    }

    public static bool LooksPlain(byte[] bytes)
        => bytes.Length >= AppMarker.Length && bytes.AsSpan(0, AppMarker.Length).SequenceEqual(AppMarker);

    public static int KindSize(ValueKind kind) => kind switch
    {
        ValueKind.U8 => 1,
        ValueKind.BE16 => 2,
        _ => 4,
    };

    private static void CheckBounds(byte[] bytes, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset + size > bytes.Length)
            throw new InvalidDataException($"Offset 0x{offset:X} is outside APP.DAT");
    }

    public static uint ReadValue(byte[] bytes, int offset, ValueKind kind)
    {
        int size = KindSize(kind);
        CheckBounds(bytes, offset, size);
        return kind switch
        {
            ValueKind.U8 => bytes[offset],
            ValueKind.BE16 => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2)),
            _ => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)),
        };
    }

    public static void WriteValue(byte[] bytes, int offset, ValueKind kind, uint value)
    {
        int size = KindSize(kind);
        CheckBounds(bytes, offset, size);
        switch (kind)
        {
            case ValueKind.U8:
                if (value > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                bytes[offset] = (byte)value;
                break;
            case ValueKind.BE16:
                if (value > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), (ushort)value);
                break;
            default:
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
                break;
        }
    }

    public static byte[] ApplyFieldTransaction(byte[] source, IEnumerable<FieldWrite> writes)
    {
        byte[] work = (byte[])source.Clone();
        foreach (var write in writes)
        {
            if (write.Value > write.Max)
                throw new ArgumentOutOfRangeException(nameof(writes), $"Value {write.Value} exceeds maximum {write.Max}");
            WriteValue(work, write.Offset, write.Kind, write.Value);
        }
        return RepairChecksum(work);
    }

    public static string SafeAscii(ReadOnlySpan<byte> raw)
    {
        int length = 0;
        while (length < raw.Length && raw[length] != 0)
        {
            if (raw[length] < 32 || raw[length] >= 127) return "";
            length++;
        }
        return Encoding.ASCII.GetString(raw[..length]);
    }

    public static string MonsterIdForSlot(byte[] bytes, int slot)
    {
        int offset = MonsterBase + slot * MonsterStride - MonsterIdDelta;
        if (offset < 0 || offset + 16 > bytes.Length) return "";
        return SafeAscii(bytes.AsSpan(offset, 16));
    }

    public static int MonsterRecordStart(int slot)
    {
        if (slot < 0 || slot >= MonsterSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return MonsterBase + slot * MonsterStride - MonsterIdDelta;
    }

    public static int MonsterAbilityOffset(int slot, int ability)
    {
        if (slot < 0 || slot >= MonsterSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        if (ability < 0 || ability >= MonsterAbilityCount) throw new ArgumentOutOfRangeException(nameof(ability));
        return MonsterBase + slot * MonsterStride - MonsterAbilityDelta + ability * MonsterAbilitySize;
    }

    public static string[] MonsterAbilityIds(byte[] bytes, int slot)
    {
        string[] output = new string[MonsterAbilityCount];
        for (int i = 0; i < MonsterAbilityCount; i++)
        {
            int offset = MonsterAbilityOffset(slot, i);
            output[i] = offset >= 0 && offset + MonsterAbilitySize <= bytes.Length
                ? SafeAscii(bytes.AsSpan(offset, MonsterAbilitySize))
                : "";
        }
        return output;
    }

    public static void WriteMonsterAbility(byte[] bytes, int slot, int ability, string id)
    {
        int offset = MonsterAbilityOffset(slot, ability);
        CheckBounds(bytes, offset, MonsterAbilitySize);
        Array.Clear(bytes, offset, MonsterAbilitySize);
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_]+$")) throw new InvalidDataException("Ability id contains unsupported characters");
        byte[] encoded = Encoding.ASCII.GetBytes(id);
        if (encoded.Length > MonsterAbilitySize)
            throw new InvalidDataException($"Ability id '{id}' is {encoded.Length} bytes; monster ability slots are {MonsterAbilitySize} bytes");
        encoded.CopyTo(bytes, offset);
    }

    public static int NonEmptyMonsterAbilityCount(byte[] bytes, int slot)
        => MonsterAbilityIds(bytes, slot)
            .Skip(MonsterFirstSkillSlot)
            .Count(id => !string.IsNullOrEmpty(id));

    public static int NonEmptyMonsterPassiveCount(byte[] bytes, int slot)
        => MonsterPassiveIds(bytes, slot).Count(id => !string.IsNullOrEmpty(id));

    // Real BLES01269 before/after captures prove two adjacent U8 counters in the monster
    // record tail:
    //   record+0x286 = occupied active-skill count (raw ability entries 1..32 only)
    //   record+0x287 = occupied passive count (10 passive entries)
    // The previously guessed 0x276-0x27B detector was wrong and could select unrelated data.
    // Keep a validation step so unsupported layouts remain locked rather than blindly written.
    public static MonsterAbilityCountInfo? DetectMonsterAbilityCountField(byte[] bytes, IReadOnlyList<MonsterRecord> monsters)
    {
        if (monsters.Count == 0) return null;
        int matches = 0;
        int samples = 0;
        foreach (MonsterRecord monster in monsters)
        {
            int start = MonsterRecordStart(monster.Slot);
            int activeOffset = start + MonsterActiveCountRelative;
            int passiveOffset = start + MonsterPassiveCountRelative;
            if (activeOffset < 0 || passiveOffset >= bytes.Length) continue;

            int activeField = bytes[activeOffset];
            int passiveField = bytes[passiveOffset];
            if (activeField > MonsterEditableAbilityCount || passiveField > MonsterPassiveCount) continue;

            int activeActual = NonEmptyMonsterAbilityCount(bytes, monster.Slot);
            int passiveActual = NonEmptyMonsterPassiveCount(bytes, monster.Slot);
            samples++;
            if (activeField == activeActual && passiveField == passiveActual) matches++;
        }

        int minimum = Math.Min(3, monsters.Count);
        if (samples < minimum || matches < minimum) return null;
        double confidence = (double)matches / samples;
        if (confidence < 0.90) return null;

        return new MonsterAbilityCountInfo
        {
            RelativeOffset = MonsterActiveCountRelative,
            Kind = ValueKind.U8,
            CountsTotalAbilities = true, // compatibility flag: both exact counters are verified.
            Matches = matches,
            Samples = samples
        };
    }

    public static void UpdateMonsterAbilityCount(byte[] bytes, int slot, MonsterAbilityCountInfo? info)
    {
        if (info is null) return;
        int start = MonsterRecordStart(slot);
        int active = NonEmptyMonsterAbilityCount(bytes, slot);
        int passive = NonEmptyMonsterPassiveCount(bytes, slot);
        WriteValue(bytes, start + MonsterActiveCountRelative, ValueKind.U8, (uint)active);
        WriteValue(bytes, start + MonsterPassiveCountRelative, ValueKind.U8, (uint)passive);
    }

    public static int AddMonsterAbility(byte[] bytes, int slot, string id, MonsterAbilityCountInfo? countInfo)
    {
        if (countInfo is null) throw new InvalidOperationException("Monster active/passive counters are not verified for this save. Replace an existing skill instead of adding a new one.");
        string[] ids = MonsterAbilityIds(bytes, slot);
        for (int i = MonsterFirstSkillSlot; i < ids.Length; i++)
            if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return i;

        int empty = -1;
        for (int i = MonsterFirstSkillSlot; i < ids.Length; i++)
        {
            if (string.IsNullOrEmpty(ids[i])) { empty = i; break; }
        }
        if (empty < 0) throw new InvalidOperationException($"All {MonsterEditableAbilityCount} active skill slots are full");

        WriteMonsterAbility(bytes, slot, empty, id);
        UpdateMonsterAbilityCount(bytes, slot, countInfo);
        return empty;
    }

    public static void RemoveMonsterAbilityAt(byte[] bytes, int slot, int ability, MonsterAbilityCountInfo? countInfo)
    {
        if (countInfo is null) throw new InvalidOperationException("Monster active/passive counters are not verified for this save. Removing skills is disabled; replacement is still available.");
        string[] ids = MonsterAbilityIds(bytes, slot);
        if (ability < MonsterFirstSkillSlot || ability >= ids.Length)
            throw new ArgumentOutOfRangeException(nameof(ability), "The reserved raw ability entry cannot be edited.");

        for (int i = ability; i < MonsterAbilityCount - 1; i++) WriteMonsterAbility(bytes, slot, i, ids[i + 1]);
        WriteMonsterAbility(bytes, slot, MonsterAbilityCount - 1, "");
        UpdateMonsterAbilityCount(bytes, slot, countInfo);
    }

    public static string[] MergeCopiedEditableSkillsPreservingFeral(IReadOnlyList<string> currentEditable, IReadOnlyList<string> copiedEditable)
    {
        if (currentEditable.Count != MonsterEditableAbilityCount)
            throw new ArgumentException($"Current editable skill list must contain exactly {MonsterEditableAbilityCount} slots.", nameof(currentEditable));
        if (copiedEditable.Count != MonsterEditableAbilityCount)
            throw new ArgumentException($"Copied editable skill list must contain exactly {MonsterEditableAbilityCount} slots.", nameof(copiedEditable));

        string[] output = new string[MonsterEditableAbilityCount];
        int preservedFeral = 0;
        for (int i = 0; i < currentEditable.Count; i++)
        {
            if (!string.Equals(currentEditable[i], "rk000", StringComparison.OrdinalIgnoreCase)) continue;
            output[i] = currentEditable[i];
            preservedFeral++;
        }

        List<string> source = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in copiedEditable)
        {
            if (string.IsNullOrEmpty(id) || string.Equals(id, "rk000", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(id)) source.Add(id);
        }
        if (source.Count > MonsterEditableAbilityCount - preservedFeral)
            throw new InvalidOperationException("Copied ability set is too large after preserving Feral Link in its current slot.");

        int sourceIndex = 0;
        for (int i = 0; i < output.Length && sourceIndex < source.Count; i++)
        {
            if (!string.IsNullOrEmpty(output[i])) continue;
            output[i] = source[sourceIndex++];
        }
        return output;
    }

    public static byte MonsterRoleCode(byte[] bytes, int slot)
    {
        int offset = MonsterRecordStart(slot) + MonsterRoleRelative;
        CheckBounds(bytes, offset, 1);
        return bytes[offset];
    }

    public static string MonsterRoleName(byte[] bytes, int slot)
        => MonsterRoleCode(bytes, slot) switch
        {
            0x01 => "Sentinel",
            0x06 => "Commando",
            0x0B => "Ravager",
            0x10 => "Synergist", // inferred from the observed 5-step role-code sequence
            0x15 => "Saboteur",
            0x1A => "Medic",
            _ => ""
        };

    public static int MonsterPassiveOffset(int slot, int passive)
        => MonsterBase + slot * MonsterStride - MonsterPassiveDelta + passive * MonsterPassiveSize;

    public static string[] MonsterPassiveIds(byte[] bytes, int slot)
    {
        string[] output = new string[MonsterPassiveCount];
        for (int i = 0; i < MonsterPassiveCount; i++)
        {
            int offset = MonsterPassiveOffset(slot, i);
            output[i] = offset >= 0 && offset + MonsterPassiveSize <= bytes.Length
                ? SafeAscii(bytes.AsSpan(offset, MonsterPassiveSize))
                : "";
        }
        return output;
    }

    public static void WriteMonsterPassive(byte[] bytes, int slot, int passive, string id)
    {
        if (passive < 0 || passive >= MonsterPassiveCount)
            throw new ArgumentOutOfRangeException(nameof(passive), "Passive slot must be 0-9");
        int offset = MonsterPassiveOffset(slot, passive);
        CheckBounds(bytes, offset, MonsterPassiveSize);
        Array.Clear(bytes, offset, MonsterPassiveSize);
        if (string.IsNullOrEmpty(id)) return;
        byte[] encoded = Encoding.ASCII.GetBytes(id);
        if (encoded.Length > MonsterPassiveSize) throw new InvalidDataException("Passive id too long");
        encoded.CopyTo(bytes, offset);
    }

    public static int[] AdvancedPassivePasteChangeSlots(IReadOnlyList<string> current, IReadOnlyList<string> copied)
    {
        if (current.Count != MonsterPassiveCount || copied.Count != MonsterPassiveCount)
            throw new ArgumentException($"Passive paste lists must contain exactly {MonsterPassiveCount} slots.");
        List<int> changed = new();
        for (int i = 0; i < MonsterPassiveCount; i++)
        {
            string oldId = current[i] ?? "";
            string newId = copied[i] ?? "";
            if (string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) continue;
            if (oldId.StartsWith("awp_", StringComparison.OrdinalIgnoreCase)
                || newId.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
                changed.Add(i);
        }
        return changed.ToArray();
    }

    public static MonsterRecord MonsterRecordAt(
        byte[] bytes,
        int slot,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, MonsterLevelCap>? levelCaps = null)
    {
        int baseOffset = MonsterBase + slot * MonsterStride;
        string id = MonsterIdForSlot(bytes, slot);
        names.TryGetValue(id, out string? name);
        uint maxLevel = 0;
        if (levelCaps is not null && levelCaps.TryGetValue(id, out MonsterLevelCap? cap) && cap is not null)
            maxLevel = cap.MaxLevel;
        return new MonsterRecord
        {
            Slot = slot,
            Id = id,
            Name = name ?? "",
            Level = ReadValue(bytes, baseOffset, ValueKind.U8),
            MaxLevel = maxLevel,
            HP = ReadValue(bytes, baseOffset + 0x0E, ValueKind.BE32),
            Strength = ReadValue(bytes, baseOffset + 0x12, ValueKind.BE32),
            Magic = ReadValue(bytes, baseOffset + 0x16, ValueKind.BE32),
            ATB = ReadValue(bytes, baseOffset + 0x1D, ValueKind.U8),
        };
    }

    public static bool BagRecordPresent(byte[] bytes, int bag, int slot)
    {
        if (bag < 0 || bag >= InventoryBags.Length || slot < 0 || slot >= InventoryBags[bag].Capacity) return false;
        var spec = InventoryBags[bag];
        int record = spec.Base + 4 + slot * 0x12;
        if (record < 0 || record + 18 > bytes.Length) return false;
        bool any = false;
        for (int i = 0; i < 16; i++) any |= bytes[record + i] != 0;
        return any && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 16, 2)) != 0;
    }

    // A monster record is editable only when the corresponding Monster Crystal ItemBag
    // slot is inside the bag's logical count and contains a live record. The raw monster
    // table has 226 records, but the verified Monster Crystal bag has only 200 slots;
    // records 200..225 therefore remain unverified and are deliberately excluded from
    // editable/owned-monster paths until their ownership/reference mechanism is proven.
    public static bool MonsterCrystalSlotOwned(byte[] bytes, int slot)
    {
        const int monsterCrystalBag = 5;
        if (slot < 0 || slot >= InventoryBags[monsterCrystalBag].Capacity) return false;
        if (slot >= BagSlotCount(bytes, monsterCrystalBag)) return false;
        return BagRecordPresent(bytes, monsterCrystalBag, slot);
    }

    public static List<MonsterRecord> ActiveMonsters(
        byte[] bytes,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, MonsterLevelCap>? levelCaps = null)
    {
        var output = new List<MonsterRecord>();
        int verifiedSlotLimit = Math.Min(MonsterSlots, InventoryBags[5].Capacity);
        for (int slot = 0; slot < verifiedSlotLimit; slot++)
        {
            if (!MonsterCrystalSlotOwned(bytes, slot)) continue;

            string id = MonsterIdForSlot(bytes, slot);
            if (string.IsNullOrEmpty(id)) continue;
            MonsterRecord record;
            try { record = MonsterRecordAt(bytes, slot, names, levelCaps); }
            catch { continue; }

            if (string.IsNullOrEmpty(record.Name))
                record = new MonsterRecord { Slot = record.Slot, Id = record.Id, Name = "Unknown Monster", Level = record.Level, MaxLevel = record.MaxLevel, HP = record.HP, Strength = record.Strength, Magic = record.Magic, ATB = record.ATB };
            output.Add(record);
        }
        return output;
    }

    // The 160 collectible-fragment ownership structure is intentionally not written here.
    // The former 0x13A0 raw-bit interpretation was disproved by real-save testing.

    public static uint FragmentSkillMask(byte[] bytes) => ReadValue(bytes, FragmentSkillsOffset, ValueKind.BE32);

    public static bool FragmentSkillUnlocked(uint mask, FragmentSkill skill)
        => skill.Bits.All(bit => (mask & (1U << (int)bit)) != 0);

    public static bool AllFragmentSkillsUnlocked(uint mask)
        => (mask & FragmentSkillsAllMask) == FragmentSkillsAllMask;

    public static void SetFragmentSkill(byte[] bytes, FragmentSkill skill, bool on)
    {
        uint mask = FragmentSkillMask(bytes);
        foreach (uint bit in skill.Bits)
        {
            if (on) mask |= 1U << (int)bit;
            else mask &= ~(1U << (int)bit);
        }
        WriteValue(bytes, FragmentSkillsOffset, ValueKind.BE32, mask);
    }

    public static void SetAllFragmentSkills(byte[] bytes, bool on)
    {
        uint mask = FragmentSkillMask(bytes);
        mask = on ? mask | FragmentSkillsAllMask : mask & ~FragmentSkillsAllMask;
        WriteValue(bytes, FragmentSkillsOffset, ValueKind.BE32, mask);
    }

    public static int BagSlotCount(byte[] bytes, int bag)
    {
        if (bag < 0 || bag >= InventoryBags.Length) return 0;
        BagSpec spec = InventoryBags[bag];
        if (spec.Base + 4 > bytes.Length) return 0;
        int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(spec.Base, 4)));
        return Math.Min(count, spec.Capacity);
    }

    public const int AdornmentBagIndex = 6;

    public static bool IsAdornmentCatalogItem(ItemCatalog item)
    {
        return item.BagIndex == AdornmentBagIndex
            && item.Id.StartsWith("e", StringComparison.OrdinalIgnoreCase)
            && item.MaxQty == 1;
    }

    public static List<int> FindAdornmentRecords(byte[] bytes, string id)
    {
        var records = new List<int>();
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("e", StringComparison.OrdinalIgnoreCase))
            return records;

        BagSpec spec = InventoryBags[AdornmentBagIndex];
        int count = BagSlotCount(bytes, AdornmentBagIndex);
        for (int slot = 0; slot < count; slot++)
        {
            int record = spec.Base + 4 + slot * 0x12;
            if (record + 18 > bytes.Length) break;
            if (SafeAscii(bytes.AsSpan(record, 16)).Equals(id, StringComparison.OrdinalIgnoreCase))
                records.Add(record);
        }
        return records;
    }

    public static bool IsAdornmentOwned(byte[] bytes, ItemCatalog item)
    {
        if (!IsAdornmentCatalogItem(item)) return false;
        foreach (int record in FindAdornmentRecords(bytes, item.Id))
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 16, 2)) > 0)
                return true;
        }
        return false;
    }

    private static void TrimAdornmentBagTail(byte[] bytes)
    {
        BagSpec spec = InventoryBags[AdornmentBagIndex];
        int count = BagSlotCount(bytes, AdornmentBagIndex);
        while (count > 0)
        {
            int tail = spec.Base + 4 + (count - 1) * 0x12;
            CheckBounds(bytes, tail, 18);
            bool empty = true;
            for (int i = 0; i < 18; i++)
            {
                if (bytes[tail + i] != 0) { empty = false; break; }
            }
            if (!empty) break;
            count--;
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(spec.Base, 4), (uint)count);
    }

    public static void SetAdornmentOwned(byte[] bytes, ItemCatalog item, bool owned)
    {
        if (!IsAdornmentCatalogItem(item))
            throw new InvalidOperationException("Selected catalog entry is not a mapped adornment.");

        BagSpec spec = InventoryBags[AdornmentBagIndex];
        List<int> existing = FindAdornmentRecords(bytes, item.Id);

        if (!owned)
        {
            foreach (int record in existing)
                Array.Clear(bytes, record, 18);
            TrimAdornmentBagTail(bytes);
            return;
        }

        if (existing.Count > 0)
        {
            int first = existing[0];
            Array.Clear(bytes, first, 18);
            byte[] idBytes = Encoding.ASCII.GetBytes(item.Id);
            if (idBytes.Length > 16) throw new InvalidDataException("Adornment ID is too long.");
            idBytes.CopyTo(bytes, first);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(first + 16, 2), 1);

            for (int i = 1; i < existing.Count; i++)
                Array.Clear(bytes, existing[i], 18);
            TrimAdornmentBagTail(bytes);
            return;
        }

        int count = BagSlotCount(bytes, AdornmentBagIndex);
        int target = -1;

        for (int slot = 0; slot < count; slot++)
        {
            int candidate = spec.Base + 4 + slot * 0x12;
            CheckBounds(bytes, candidate, 18);
            bool empty = true;
            for (int i = 0; i < 18; i++)
            {
                if (bytes[candidate + i] != 0) { empty = false; break; }
            }
            if (empty)
            {
                target = candidate;
                break;
            }
        }

        if (target < 0)
        {
            if (count >= spec.Capacity)
                throw new InvalidDataException("No free slot remains in the adornment ItemBag.");
            target = spec.Base + 4 + count * 0x12;
            CheckBounds(bytes, target, 18);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(spec.Base, 4), (uint)(count + 1));
        }

        Array.Clear(bytes, target, 18);
        byte[] id = Encoding.ASCII.GetBytes(item.Id);
        if (id.Length > 16) throw new InvalidDataException("Adornment ID is too long.");
        id.CopyTo(bytes, target);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(target + 16, 2), 1);
    }

    // v1.9.20: every catalog entry stored in a normal 18-byte ItemBag record is
    // quantity-editable. Equipment and monster-crystal bags use manager-backed
    // records and remain routed through their dedicated editors.
    public static bool IsDirectQuantityEditableBag(int bag) =>
        bag >= 0 && bag < InventoryBags.Length && InventoryBags[bag].NormalIds;

    public static bool IsSafeBulkQuantityBag(int bag) =>
        bag is 0 or 3 or 7 or 8;

    public static bool IsAdvancedInventoryBag(int bag) =>
        bag is 4 or 6; // Key Items / OOPArts and Internal / Special
 // Consumables, Components/Materials, Casino/Specialty, Monster Growth Materials


    public static bool IsQuantityEditableKeyItem(string id) => true;

    public static int FindNormalItemRecord(byte[] bytes, int bag, string id)
    {
        if (bag < 0 || bag >= InventoryBags.Length || !InventoryBags[bag].NormalIds) return -1;
        BagSpec spec = InventoryBags[bag];
        for (int slot = 0; slot < BagSlotCount(bytes, bag); slot++)
        {
            int record = spec.Base + 4 + slot * 0x12;
            if (record + 18 <= bytes.Length && SafeAscii(bytes.AsSpan(record, 16)) == id) return record;
        }
        return -1;
    }

    public static void SetCatalogQuantity(byte[] bytes, ItemCatalog item, int value)
    {
        if (!item.Editable) throw new InvalidOperationException("Item is not quantity-editable");
        if (value < 0 || value > item.MaxQty) throw new ArgumentOutOfRangeException(nameof(value), $"Quantity must be 0-{item.MaxQty}");

        int record = FindNormalItemRecord(bytes, item.BagIndex, item.Id);
        if (record >= 0 && value == 0)
        {
            Array.Clear(bytes, record, 18);
            BagSpec spec = InventoryBags[item.BagIndex];
            int count = BagSlotCount(bytes, item.BagIndex);
            while (count > 0)
            {
                int tail = spec.Base + 4 + (count - 1) * 0x12;
                if (tail + 18 > bytes.Length) break;
                bool empty = true;
                for (int i = 0; i < 18; i++) if (bytes[tail + i] != 0) { empty = false; break; }
                if (!empty) break;
                count--;
            }
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(spec.Base, 4), (uint)count);
            return;
        }

        if (record < 0)
        {
            if (value == 0) return;
            BagSpec spec = InventoryBags[item.BagIndex];
            int count = BagSlotCount(bytes, item.BagIndex);
            for (int slot = 0; slot < count; slot++)
            {
                int candidate = spec.Base + 4 + slot * 0x12;
                if (candidate + 18 > bytes.Length) break;
                bool empty = true;
                for (int i = 0; i < 16; i++) if (bytes[candidate + i] != 0) { empty = false; break; }
                if (empty && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(candidate + 16, 2)) == 0)
                {
                    record = candidate;
                    break;
                }
            }
            if (record < 0 && count < spec.Capacity)
            {
                record = spec.Base + 4 + count * 0x12;
                CheckBounds(bytes, record, 18);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(spec.Base, 4), (uint)(count + 1));
            }
            if (record < 0) throw new InvalidDataException("No free slot in inventory bag");
            byte[] id = Encoding.ASCII.GetBytes(item.Id);
            if (id.Length > 16) throw new InvalidDataException("Item id too long");
            Array.Clear(bytes, record, 18);
            id.CopyTo(bytes, record);
        }
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record + 16, 2), (ushort)value);
    }

    public static int CurrentCatalogQuantity(byte[] bytes, ItemCatalog item)
    {
        int record = FindNormalItemRecord(bytes, item.BagIndex, item.Id);
        return record < 0 ? 0 : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 16, 2));
    }

    public static string EquipmentCatalogId(byte[] bytes, int bag, int slot)
    {
        int offset = bag == 1 ? WeaponManagerBase + slot * EquipRecordSize : bag == 2 ? AccessoryManagerBase + slot * EquipRecordSize : -1;
        if (offset < 0 || offset + 14 > bytes.Length) return "";
        return SafeAscii(bytes.AsSpan(offset, 14));
    }

    public static List<ItemRecord> IterInventory(byte[] bytes, IReadOnlyDictionary<string, string> itemNames, IReadOnlyDictionary<string, string> monsterNames)
    {
        var output = new List<ItemRecord>();
        for (int bag = 0; bag < InventoryBags.Length; bag++)
        {
            BagSpec spec = InventoryBags[bag];
            for (int slot = 0; slot < BagSlotCount(bytes, bag); slot++)
            {
                int record = spec.Base + 4 + slot * 0x12;
                if (record + 18 > bytes.Length) break;
                string id = SafeAscii(bytes.AsSpan(record, 16));
                int qty = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 16, 2));
                if (string.IsNullOrEmpty(id) && qty == 0) continue;

                string resolved = id;
                itemNames.TryGetValue(id, out string? name);
                bool editable = IsDirectQuantityEditableBag(bag);
                if (bag == 1 || bag == 2)
                {
                    resolved = EquipmentCatalogId(bytes, bag, slot);
                    itemNames.TryGetValue(resolved, out name);
                    name ??= $"Equipment Instance {slot + 1}";
                    editable = false;
                }
                else if (bag == 5)
                {
                    resolved = MonsterIdForSlot(bytes, slot);
                    monsterNames.TryGetValue(resolved, out name);
                    name ??= $"Monster Crystal {slot + 1}";
                    editable = false;
                }
                else name ??= "Unknown / Unnamed Item";

                output.Add(new ItemRecord
                {
                    BagIndex = bag,
                    Slot = slot,
                    QtyOffset = record + 16,
                    Bag = spec.Name,
                    Id = id,
                    ResolvedId = resolved,
                    Name = name,
                    Qty = qty,
                    Editable = editable,
                });
            }
        }
        return output;
    }

    public static List<MonsterLevelCap> ParseMonsterLevelCaps(string data)
    {
        var output = new List<MonsterLevelCap>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = data.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 4) continue;

            string id = parts[0].Trim();
            string name = parts[1].Trim();
            string role = parts[2].Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                throw new InvalidDataException($"Monster level cap row {i + 1} has a blank id or name");
            if (!uint.TryParse(parts[3].Trim(), out uint maxLevel) || maxLevel < 1 || maxLevel > 99)
                throw new InvalidDataException($"Monster level cap row {i + 1} has an invalid max level");
            if (role is not ("COM" or "RAV" or "SEN" or "SAB" or "SYN" or "MED"))
                throw new InvalidDataException($"Monster level cap row {i + 1} has an invalid role '{role}'");
            if (!ids.Add(id))
                throw new InvalidDataException($"Duplicate monster level cap id '{id}'");

            output.Add(new MonsterLevelCap(id, name, role, maxLevel));
        }
        return output;
    }

    public static Dictionary<string, string> ParseTsvMap(string data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in data.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            int tab = line.IndexOf('\t');
            if (tab < 0) continue;
            result[line[..tab].Trim()] = line[(tab + 1)..].Trim();
        }
        return result;
    }

    public static List<ItemCatalog> ParseItemCatalog(string data)
    {
        var output = new List<ItemCatalog>();
        string[] lines = data.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 6) continue;
            if (!int.TryParse(parts[3], out int bag) || !int.TryParse(parts[4], out int max)) continue;
            string id = parts[0].Trim();
            // The TSV editable column is retained as catalog metadata, but v1.9.20
            // exposes every normal ItemBag entry for editing. Manager-backed bags
            // (weapons/accessories/monster crystals) are edited by their dedicated UI.
            bool editable = IsDirectQuantityEditableBag(bag);
            output.Add(new ItemCatalog(id, parts[1], parts[2], bag, max, editable));
        }
        return output;
    }

    public static string DescribeTrait(Trait trait)
    {
        string name = trait.Name;
        string value = trait.Value;

        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATB Advantage"] = "Starts battle with an ATB advantage so the monster can act sooner.",
            ["First Strike"] = "Fully charges the ATB gauge before battle.",
            ["Double CP"] = "Doubles CP earned from battles while this passive contributes to the reward calculation.",
            ["Pressure II"] = "Raises Cut by 10, increasing the chance that attacks interrupt an enemy's actions.",
            ["Immovable II"] = "Raises Keep by 10, reducing the chance that enemy attacks interrupt the monster's actions.",
            ["Jungle Law"] = "Changes damage by 20% based on HP comparison: stronger against enemies with lower HP, weaker against enemies with higher HP.",
            ["Role Resonance"] = "Provides a 20% offensive bonus when all three active paradigm roles are the same.",
            ["Weak Spot"] = "Increases damage by 15% when an attack targets an enemy vulnerability.",
            ["Item Scavenger II"] = "Improves the chance of obtaining item drops after battle.",
            ["Item Collector"] = "Improves item-drop rewards and collection efficiency after battle.",
            ["Gilfinder II"] = "Improves gil rewards from applicable battles.",
            ["Kill: ATB Charge"] = "Restores 50% of one ATB segment when an enemy is defeated.",
            ["Kill: Libra Charge"] = "Builds Libra/scan-related charge when the monster helps defeat an enemy.",
            ["Rapid Recovery"] = "Reduces the duration of status ailments by 25%.",
            ["Pack Mentality"] = "Adds a 30% damage bonus when every monster in the Paradigm Pack shares the same Crystarium constellation/type.",
            ["Feral Speed II"] = "Increases Feral Link gauge charge rate by 40%.",
            ["Feral Surge"] = "When the Feral Link gauge becomes full, grants Bravery, Faith, Protect, Shell, Veil, and Vigilance for 60 seconds.",
            ["Critical: Tetradefense"] = "Grants Tetradefense automatically while HP is in the critical range.",
            ["Auto-Tetradefense"] = "Starts battle with Tetradefense active.",
            ["Critical: Vigilance"] = "Grants Vigilance automatically while HP is in the critical range.",
            ["Auto-Vigilance"] = "Starts battle with Vigilance active.",
            ["Critical: Haste"] = "Grants Haste automatically while HP is in the critical range.",
            ["Auto-Haste"] = "Starts battle with Haste active.",
            ["Critical: Protect"] = "Grants Protect automatically while HP is in the critical range.",
            ["Auto-Protect"] = "Starts battle with Protect active.",
            ["Critical: Shell"] = "Grants Shell automatically while HP is in the critical range.",
            ["Auto-Shell"] = "Starts battle with Shell active.",
            ["Critical: Bravery"] = "Grants Bravery automatically while HP is in the critical range.",
            ["Auto-Bravery"] = "Starts battle with Bravery active.",
            ["Critical: Faith"] = "Grants Faith automatically while HP is in the critical range.",
            ["Auto-Faith"] = "Starts battle with Faith active.",
            ["Critical: Veil"] = "Grants Veil automatically while HP is in the critical range.",
            ["Auto-Veil"] = "Starts battle with Veil active.",
            ["Hindrance"] = "Advanced role passive. Applies a role-specific restriction/modifier; Red/Yellow Lock state is not mapped by the editor.",
            ["Fettered Magic"] = "Reduces magic damage dealt to enemies by 30%. This is an advanced awp_* passive; Red/Yellow Lock state is not mapped.",
            ["Attack ATB Charge"] = "Advanced role passive that improves ATB recovery from attacking.",
            ["Attack ATB Charge II"] = "Stronger version of Attack ATB Charge.",
            ["Improved Raise"] = "Improves the effectiveness of Raise.",
            ["Improved Raise II"] = "Stronger version of Improved Raise.",
            ["Improved Guard"] = "Improves guard effectiveness while acting as a Sentinel.",
            ["Improved Guard II"] = "Stronger version of Improved Guard.",
            ["Critical: Power Surge"] = "Grants a power increase while HP is in the critical range.",
            ["Critical: Power Surge II"] = "Stronger critical-HP power increase.",
            ["Improved Ward"] = "Improves defensive ward effectiveness.",
            ["Improved Ward II"] = "Stronger version of Improved Ward.",
            ["Augment Maintenance"] = "Extends or improves maintenance of beneficial status effects.",
            ["Augment Maintenance II"] = "Stronger version of Augment Maintenance.",
            ["Chain Bonus Boost"] = "Improves chain bonus growth during combat.",
            ["Chain Bonus Boost II"] = "Stronger version of Chain Bonus Boost.",
            ["Stagger Maintenance"] = "Helps maintain stagger/chain pressure for longer.",
            ["Stagger Maintenance II"] = "Stronger version of Stagger Maintenance.",
            ["Critical Shield"] = "Improves defense while HP is in the critical range.",
            ["Critical Shield II"] = "Stronger version of Critical Shield.",
            ["Defense Maintenance"] = "Improves maintenance of defensive effects.",
            ["Defense Maintenance II"] = "Stronger version of Defense Maintenance.",
            ["Improved Cure"] = "Improves HP restored by Cure-family actions.",
            ["Improved Cure II"] = "Stronger version of Improved Cure.",
            ["Ally KO: Power Surge"] = "Grants a power increase when an ally is knocked out.",
            ["Ally KO: Power Surge II"] = "Stronger version of Ally KO: Power Surge.",
            ["Improved Debuffing"] = "Improves effectiveness of debuffing actions.",
            ["Improved Debuffing II"] = "Stronger version of Improved Debuffing.",
            ["Improved Debilitation"] = "Improves effectiveness of debilitating/status-inflicting actions.",
            ["Improved Debilitation II"] = "Stronger version of Improved Debilitation.",
            ["Improved Counter"] = "Improves counterattack performance.",
            ["Improved Counter II"] = "Stronger version of Improved Counter."
        };

        if (direct.TryGetValue(name, out string? description))
            return description;

        if (name.StartsWith("HP +", StringComparison.OrdinalIgnoreCase))
            return $"Raises maximum HP by {value}%.";
        if (name.StartsWith("Strength +", StringComparison.OrdinalIgnoreCase))
            return $"Raises Strength by {value}%.";
        if (name.StartsWith("Magic +", StringComparison.OrdinalIgnoreCase))
            return $"Raises Magic by {value}%.";

        if (name.StartsWith("Resist Physical/Magic ", StringComparison.OrdinalIgnoreCase))
            return $"Reduces physical and magical damage taken by approximately {value}%.";
        if (name.StartsWith("Resist Physical ", StringComparison.OrdinalIgnoreCase))
            return $"Reduces physical damage taken by approximately {value}%.";
        if (name.StartsWith("Resist Magic ", StringComparison.OrdinalIgnoreCase))
            return $"Reduces magical damage taken by approximately {value}%.";
        if (name.StartsWith("Resist Fire ", StringComparison.OrdinalIgnoreCase))
            return $"Raises Fire resistance by {value}%.";
        if (name.StartsWith("Resist Ice ", StringComparison.OrdinalIgnoreCase))
            return $"Raises Ice resistance by {value}%.";
        if (name.StartsWith("Resist Wind ", StringComparison.OrdinalIgnoreCase))
            return $"Raises Wind resistance by {value}%.";
        if (name.StartsWith("Resist Lightning ", StringComparison.OrdinalIgnoreCase))
            return $"Raises Lightning resistance by {value}%.";
        if (name.StartsWith("Resist All Elements ", StringComparison.OrdinalIgnoreCase))
            return $"Raises resistance to all four elements by {value}%.";

        if (name.StartsWith("Resist ", StringComparison.OrdinalIgnoreCase) &&
            trait.Category.Equals("Status Resistance", StringComparison.OrdinalIgnoreCase))
        {
            string status = name["Resist ".Length..];
            int pct = status.LastIndexOf('%');
            if (pct >= 0)
            {
                int space = status.LastIndexOf(' ', pct);
                if (space >= 0) status = status[..space];
            }
            return $"Raises resistance to {status} by {value}%.";
        }

        if (name.StartsWith("Resilience ", StringComparison.OrdinalIgnoreCase))
            return $"Raises general resistance to negative status effects by {value}%.";

        if (trait.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
            return $"Advanced / role passive: {name}. Exact Red/Yellow Lock metadata is not mapped, so replacement should be intentional.";

        return $"{trait.Category}: {name}" + (string.IsNullOrWhiteSpace(value) || value == "-" ? "." : $" ({value}).");
    }

    public static List<Trait> ParseTraits(string data)
    {
        var output = new List<Trait>();
        string[] lines = data.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] p = line.Split('\t');
            if (p.Length >= 5) output.Add(new Trait(p[0], p[1], p[2], p[3], p[4]));
        }
        return output;
    }

    public static List<MonsterSkill> ParseMonsterSkills(string data)
    {
        var output = new List<MonsterSkill>();
        string[] lines = data.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] p = line.Split('\t');
            if (p.Length >= 4) output.Add(new MonsterSkill(p[0], p[1], p[2], p[3]));
        }
        return output;
    }

    public static List<AccessoryEffect> ParseAccessoryEffects(string data)
    {
        var output = new List<AccessoryEffect>();
        string[] lines = data.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] p = line.Split('\t');
            if (p.Length < 5 || !int.TryParse(p[4], out int capacity)) continue;
            output.Add(new AccessoryEffect(p[0], p[1], p[2], p[3], capacity));
        }
        return output;
    }

    public static int ResolveTraitWriteTarget(
        byte[] bytes,
        int slot,
        Trait trait,
        IReadOnlyDictionary<string, Trait> traits,
        int preferredReplaceSlot = -1,
        bool allowAdvancedOverwrite = false)
    {
        string[] ids = MonsterPassiveIds(bytes, slot);
        int familyTarget = -1;
        int empty = -1;

        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            if (string.Equals(id, trait.Id, StringComparison.OrdinalIgnoreCase)) return i;
            if (string.IsNullOrEmpty(id))
            {
                if (empty < 0) empty = i;
                continue;
            }

            if (!string.IsNullOrEmpty(trait.Family)
                && traits.TryGetValue(id, out Trait? old)
                && string.Equals(old.Family, trait.Family, StringComparison.OrdinalIgnoreCase))
            {
                // Existing awp_* slots are preserved conservatively during automatic family replacement.
                // The awp_* prefix identifies an advanced/role-passive namespace; it does NOT
                // prove Red/Yellow Lock state. The caller may explicitly allow direct overwrite.
                if (!id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase) || allowAdvancedOverwrite)
                {
                    familyTarget = i;
                    break;
                }
            }
        }

        if (familyTarget >= 0) return familyTarget;
        if (empty >= 0) return empty;

        if (preferredReplaceSlot >= 0 && preferredReplaceSlot < MonsterPassiveCount)
        {
            string existing = ids[preferredReplaceSlot];
            if (!allowAdvancedOverwrite && existing.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected current passive uses an advanced awp_* ID. Its actual Red/Yellow Lock state is not mapped. Use Replace Selected if you intentionally want to overwrite that slot.");
            return preferredReplaceSlot;
        }

        throw new InvalidOperationException("All 10 passive slots are full. Select a current passive slot to replace, then click Add / Upgrade or Replace Selected.");
    }

    public static int AddOrReplaceTrait(
        byte[] bytes,
        int slot,
        Trait trait,
        IReadOnlyDictionary<string, Trait> traits,
        int preferredReplaceSlot = -1,
        bool allowAdvancedOverwrite = false)
    {
        int target = ResolveTraitWriteTarget(bytes, slot, trait, traits, preferredReplaceSlot, allowAdvancedOverwrite);
        WriteMonsterPassive(bytes, slot, target, trait.Id);
        return target;
    }

    public static void RemovePassiveAt(byte[] bytes, int slot, int passive)
    {
        string[] ids = MonsterPassiveIds(bytes, slot);
        if (passive < 0 || passive >= ids.Length) throw new ArgumentOutOfRangeException(nameof(passive));
        // Do not compact passive slots. Hidden Red/Yellow Lock metadata is not mapped, so
        // relocating neighboring passives (especially awp_* entries) is less safe than
        // clearing only the slot the user explicitly selected.
        WriteMonsterPassive(bytes, slot, passive, "");
    }

    public static bool IsResistanceCategory(string category)
        => category.Contains("Resistance", StringComparison.OrdinalIgnoreCase) || category.Contains("resist", StringComparison.OrdinalIgnoreCase);

    public static void ClearResistanceTraits(byte[] bytes, int slot, IReadOnlyDictionary<string, Trait> traits)
    {
        string[] ids = MonsterPassiveIds(bytes, slot);
        // Clear only the mapped resistance slots. Never compact/reorder unrelated passives;
        // their hidden per-slot lock metadata is not mapped.
        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            if (string.IsNullOrEmpty(id)) continue;
            if (traits.TryGetValue(id, out Trait? trait) && IsResistanceCategory(trait.Category))
                WriteMonsterPassive(bytes, slot, i, "");
        }
    }

    public static byte[] GenerateHashKeyForSecureFileId(byte[] secureFileId)
    {
        byte[] secure = new byte[16];
        Array.Copy(secureFileId, secure, Math.Min(secureFileId.Length, 16));
        byte[] key = new byte[20];
        int j = 0;
        for (int i = 0; i < 20; i++)
        {
            key[i] = i switch
            {
                1 => 11,
                2 => 15,
                5 => 14,
                8 => 10,
                _ => secure[j++],
            };
        }
        return key;
    }

    public static List<PfdEntry> ParsePfdEntries(byte[] pfd)
    {
        if (pfd.Length < 0x8000 || BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(0, 8)) != 0x50464442UL)
            throw new InvalidDataException("Invalid PARAM.PFD");
        ulong version = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(8, 8));
        if (version != 3UL && version != 4UL) throw new InvalidDataException($"Unsupported PFD version {version}");

        const ulong tableStart = 0x60 + 24;
        int countOffset = 0x60;
        ulong capacity = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(countOffset, 8));
        ulong reserved = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(countOffset + 8, 8));
        ulong used = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(countOffset + 16, 8));
        ulong fileLength = (ulong)pfd.Length;
        if (used > reserved || capacity > (fileLength - tableStart) / 8) throw new InvalidDataException("Malformed or truncated PARAM.PFD");
        ulong offset64 = tableStart + capacity * 8;
        if (offset64 > fileLength || reserved > (fileLength - offset64) / 0x110) throw new InvalidDataException("Malformed or truncated PARAM.PFD");
        ulong entriesEnd = offset64 + reserved * 0x110;
        if (capacity > (fileLength - entriesEnd) / 20) throw new InvalidDataException("Malformed or truncated PARAM.PFD");

        int offset = checked((int)offset64);
        var output = new List<PfdEntry>();
        for (ulong i = 0; i < used; i++)
        {
            int start = offset;
            offset += 8;
            string name = Encoding.ASCII.GetString(pfd, offset, 65);
            int zero = name.IndexOf('\0');
            if (zero >= 0) name = name[..zero];
            offset += 65 + 7;
            byte[] key = pfd.AsSpan(offset, 64).ToArray();
            offset += 64 + 80 + 40;
            ulong size = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(offset, 8));
            offset += 8;
            if (offset - start != 0x110) throw new InvalidDataException("Unexpected PFD entry size");
            output.Add(new PfdEntry(name, key, size));
        }
        return output;
    }

    public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] data)
    {
        if (key.Length != 16 || iv.Length != 16 || data.Length % 16 != 0) throw new InvalidDataException("Bad AES-CBC input");
        using Aes aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    public static byte[] DecryptSecureFile(byte[] data, byte[] keyMaterial)
    {
        if (keyMaterial.Length < 16) throw new InvalidDataException("Short file key");
        byte[] key = keyMaterial[..16];
        byte[] output = new byte[data.Length];
        using Aes aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        using ICryptoTransform decryptor = aes.CreateDecryptor();

        int blocks = data.Length / 16;
        for (int i = 0; i < blocks; i++)
        {
            byte[] counter = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(counter.AsSpan(0, 8), (ulong)i);
            byte[] counterKey = new byte[16];
            encryptor.TransformBlock(counter, 0, 16, counterKey, 0);
            byte[] decrypted = new byte[16];
            decryptor.TransformBlock(data, i * 16, 16, decrypted, 0);
            for (int j = 0; j < 16; j++) output[i * 16 + j] = (byte)(decrypted[j] ^ counterKey[j]);
        }
        Array.Copy(data, blocks * 16, output, blocks * 16, data.Length - blocks * 16);
        return output;
    }

    public static string DetectTitleId(string folder)
    {
        var regex = new Regex("(?i)(BLES|BLUS|BLJM|BCAS)\\d{5}", RegexOptions.Compiled);
        Match match = regex.Match(Path.GetFileName(folder));
        if (match.Success) return match.Value.ToUpperInvariant();
        string sfo = Path.Combine(folder, "PARAM.SFO");
        if (File.Exists(sfo))
        {
            string raw = Encoding.Latin1.GetString(File.ReadAllBytes(sfo));
            match = regex.Match(raw);
            if (match.Success) return match.Value.ToUpperInvariant();
        }
        return "";
    }

    public static List<string> PfdEntryNamesFromFolder(string folder)
        => ParsePfdEntries(File.ReadAllBytes(Path.Combine(folder, "PARAM.PFD"))).Select(e => e.Name).ToList();

    public static RegionSupport RegionSupportForTitle(string title) => title.ToUpperInvariant() switch
    {
        "BLES01269" => RegionSupport.Tested,
        "BLUS30776" => RegionSupport.SupportedUnverified,
        "BCAS20224" => RegionSupport.SupportedUnverified,
        _ => RegionSupport.Unknown,
    };

    public static (byte[] Key, string Title, List<string> Names) DecryptKeyDatFromFolder(string folder)
    {
        byte[] pfd = File.ReadAllBytes(Path.Combine(folder, "PARAM.PFD"));
        byte[] keyDat = File.ReadAllBytes(Path.Combine(folder, "KEY.DAT"));
        string title = DetectTitleId(folder);
        if (!SecureFileIds.TryGetValue(title, out byte[]? secureFileId)) throw new InvalidDataException($"Secure-file ID for {title} not built in");
        List<PfdEntry> entries = ParsePfdEntries(pfd);
        List<string> names = entries.Select(e => e.Name).ToList();
        PfdEntry? entry = entries.FirstOrDefault(e => e.Name.Equals("KEY.DAT", StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new InvalidDataException("KEY.DAT not listed in PARAM.PFD");
        byte[] hash = GenerateHashKeyForSecureFileId(secureFileId);
        byte[] entryKey = AesCbcDecrypt(SysconManagerKey, hash[..16], entry.Key);
        byte[] plain = DecryptSecureFile(keyDat, entryKey);
        if (plain.Length != 16) throw new InvalidDataException($"Decrypted KEY.DAT is {plain.Length} bytes");
        return (plain, title, names);
    }

    public static SaveLoadResult LoadSaveFolder(string folder)
    {
        folder = ResolveSaveFolder(folder);
        string appPath = Path.Combine(folder, "APP.DAT");
        string keyPath = Path.Combine(folder, "KEY.DAT");
        byte[] app = File.ReadAllBytes(appPath);
        if (app.Length % 8 != 0) throw new InvalidDataException("APP.DAT size must be multiple of 8");

        string title = DetectTitleId(folder);
        string pfdPath = Path.Combine(folder, "PARAM.PFD");
        bool pfdExists = File.Exists(pfdPath);
        bool pfdMetadataReliable = !pfdExists;
        List<string> pfdNames = new();
        if (pfdExists)
        {
            try
            {
                pfdNames = PfdEntryNamesFromFolder(folder);
                pfdMetadataReliable = true;
            }
            catch
            {
                // Keep the save readable when possible, but never infer protection state from an
                // unreadable PFD. Plain APP.DAT cannot validate a candidate encryption key.
                pfdMetadataReliable = false;
            }
        }
        bool keyProtected = pfdMetadataReliable && pfdNames.Any(n => n.Equals("KEY.DAT", StringComparison.OrdinalIgnoreCase));
        byte[] rawKey = File.Exists(keyPath) ? File.ReadAllBytes(keyPath) : Array.Empty<byte>();
        var candidates = new List<(byte[] Key, string Label, bool TrustedForPlain)>();
        if (keyProtected)
        {
            try
            {
                var decrypted = DecryptKeyDatFromFolder(folder);
                candidates.Add((decrypted.Key, "KEY.DAT auto-decrypted via PARAM.PFD", true));
            }
            catch { }
        }
        else if (rawKey.Length == 16 && pfdMetadataReliable)
        {
            candidates.Add((rawKey, "KEY.DAT already usable", true));
        }
        else if (rawKey.Length == 16 && !pfdMetadataReliable)
        {
            // For encrypted APP.DAT this candidate can still be validated by successful decryption
            // plus the db_partyman marker. It must never be trusted for an already-plaintext APP.
            candidates.Add((rawKey, "raw KEY.DAT validated only by encrypted APP", false));
        }

        if (LooksPlain(app))
        {
            foreach (var candidate in candidates.Where(c => c.TrustedForPlain))
            {
                try
                {
                    ulong key = DeriveWorkingKey(candidate.Key);
                    return new SaveLoadResult
                    {
                        Folder = folder,
                        Plain = (byte[])app.Clone(),
                        WorkingKey = key,
                        Mode = "Decrypted/plain APP.DAT | " + candidate.Label,
                        TitleId = title,
                        PfdProtected = keyProtected,
                        PfdMetadataReliable = pfdMetadataReliable,
                        PfdNames = pfdNames,
                        FragmentCount = ReadSfoFragmentCount(folder),
                    };
                }
                catch { }
            }
            return new SaveLoadResult
            {
                Folder = folder,
                Plain = (byte[])app.Clone(),
                WorkingKey = 0,
                Mode = "Decrypted/plain APP.DAT | read-only (no trustworthy encryption key)",
                TitleId = title,
                PfdProtected = keyProtected,
                PfdMetadataReliable = pfdMetadataReliable,
                PfdNames = pfdNames,
                FragmentCount = ReadSfoFragmentCount(folder),
            };
        }

        foreach (var candidate in candidates)
        {
            try
            {
                ulong key = DeriveWorkingKey(candidate.Key);
                byte[] decrypted = DecryptBytes(app, key);
                if (LooksPlain(decrypted))
                {
                    return new SaveLoadResult
                    {
                        Folder = folder,
                        Plain = decrypted,
                        WorkingKey = key,
                        Mode = "Encrypted APP.DAT | " + candidate.Label,
                        TitleId = title,
                        PfdProtected = keyProtected,
                        PfdMetadataReliable = pfdMetadataReliable,
                        PfdNames = pfdNames,
                        FragmentCount = ReadSfoFragmentCount(folder),
                    };
                }
            }
            catch { }
        }
        throw new InvalidDataException($"Could not decrypt APP.DAT. KEY.DAT may be protected, from another save, or unsupported for region {(string.IsNullOrEmpty(title) ? "unknown" : title)}");
    }

    public static int ReadSfoFragmentCount(string folder)
    {
        string path = Path.Combine(folder, "PARAM.SFO");
        if (!File.Exists(path)) return -1;
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Match match = Regex.Match(raw, @"Total fragments:\s*(\d{1,3})/160");
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) ? count : -1;
    }

    public static int[] CharacterEquipmentReferences(byte[] bytes, string character)
    {
        if (!CharacterEquipmentReferenceBase.TryGetValue(character, out int offset))
            throw new ArgumentException("Character must be Serah or Noel", nameof(character));
        CheckBounds(bytes, offset, CharacterEquipmentReferenceCount * 4);
        var refs = new int[CharacterEquipmentReferenceCount];
        for (int i = 0; i < refs.Length; i++)
        {
            uint raw = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + i * 4, 4));
            refs[i] = raw == uint.MaxValue ? -1 : checked((int)raw);
        }
        return refs;
    }

    public static int CharacterEquippedWeaponSlot(byte[] bytes, string character)
    {
        int index = CharacterEquipmentReferences(bytes, character)[0];
        return index >= WeaponManagerIndexBase ? index - WeaponManagerIndexBase : -1;
    }

    public static List<(int EquipPosition, int ManagerSlot)> CharacterEquippedAccessoryReferences(byte[] bytes, string character)
    {
        int[] refs = CharacterEquipmentReferences(bytes, character);
        var output = new List<(int EquipPosition, int ManagerSlot)>();
        var seen = new HashSet<int>();
        int end = Math.Min(refs.Length, CharacterAccessoryReferenceStart + CharacterAccessorySlotCount);
        for (int i = CharacterAccessoryReferenceStart; i < end; i++)
        {
            int slot = refs[i];
            if (slot < 0) continue;
            if (slot >= AccessoryManagerSlots)
                throw new InvalidDataException($"{character} accessory reference {i} points outside the accessory manager: {slot}");
            if (!seen.Add(slot))
                throw new InvalidDataException($"{character} accessory references contain duplicate manager slot {slot + 1}");
            if (slot >= BagSlotCount(bytes, 2))
                throw new InvalidDataException($"{character} accessory reference {i} points beyond the logical Accessories ItemBag count: slot {slot + 1}");
            if (!BagRecordPresent(bytes, 2, slot))
                throw new InvalidDataException($"{character} accessory reference {i} points to manager slot {slot + 1}, but the Accessories ItemBag does not own that instance");
            string id = EquipmentCatalogId(bytes, 2, slot);
            if (!id.StartsWith("acc_", StringComparison.Ordinal))
                throw new InvalidDataException($"{character} accessory reference {i} does not resolve to an accessory");
            uint flags = EquipmentFlags(bytes, 2, slot);
            if ((flags & EquippedFlag) == 0)
                throw new InvalidDataException($"{character} accessory reference {i} points to an instance that is not marked equipped");
            output.Add((i, slot));
        }
        return output;
    }

    public static List<int> CharacterEquippedAccessorySlots(byte[] bytes, string character)
        => CharacterEquippedAccessoryReferences(bytes, character).Select(r => r.ManagerSlot).ToList();

    public static uint EquipmentFlags(byte[] bytes, int bag, int slot)
    {
        int offset = bag == 1 ? WeaponManagerBase + slot * EquipRecordSize : bag == 2 ? AccessoryManagerBase + slot * EquipRecordSize : -1;
        if (offset < 0) throw new InvalidOperationException("Equipment bag must be Weapons or Accessories");
        CheckBounds(bytes, offset, EquipRecordSize);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 14, 4));
    }

    public static bool CharacterReferencesAccessory(byte[] bytes, string character, int managerSlot)
        => CharacterEquippedAccessorySlots(bytes, character).Contains(managerSlot);

    public static byte[] ReplaceCharacterAccessory(byte[] source, string character, int managerSlot, string newId)
    {
        if (!CharacterReferencesAccessory(source, character, managerSlot))
            throw new InvalidOperationException($"Accessory instance {managerSlot + 1} is not equipped by {character}");
        if (!newId.StartsWith("acc_", StringComparison.Ordinal))
            throw new InvalidOperationException("Character passive/resistance replacement must be an accessory");

        string otherCharacter = character.Equals("Serah", StringComparison.OrdinalIgnoreCase) ? "Noel" : "Serah";
        if (CharacterReferencesAccessory(source, otherCharacter, managerSlot))
            throw new InvalidDataException($"Accessory instance {managerSlot + 1} is referenced by both {character} and {otherCharacter}; refusing a shared-instance replacement");

        byte[] work = (byte[])source.Clone();
        Dictionary<string, int[]> refsBefore = CharacterEquipmentReferenceBase.Keys
            .ToDictionary(name => name, name => CharacterEquipmentReferences(work, name), StringComparer.OrdinalIgnoreCase);
        uint flagsBefore = EquipmentFlags(work, 2, managerSlot);
        if (!BagRecordPresent(work, 2, managerSlot))
            throw new InvalidDataException("Accessory instance is not owned by the Accessories ItemBag");

        WriteEquipmentCatalogId(work, 2, managerSlot, newId);

        foreach (var pair in refsBefore)
        {
            if (!pair.Value.SequenceEqual(CharacterEquipmentReferences(work, pair.Key)))
                throw new InvalidOperationException("Accessory replacement changed character equipment references");
        }
        if (EquipmentFlags(work, 2, managerSlot) != flagsBefore)
            throw new InvalidOperationException("Accessory replacement changed equipment flags");
        if (!BagRecordPresent(work, 2, managerSlot))
            throw new InvalidOperationException("Accessory replacement changed ItemBag ownership");
        if (!string.Equals(EquipmentCatalogId(work, 2, managerSlot), newId, StringComparison.Ordinal))
            throw new InvalidOperationException("Accessory replacement failed catalog-ID verification");
        return RepairChecksum(work);
    }

    public static List<EquipmentRecord> OwnedEquipment(byte[] bytes, IReadOnlyDictionary<string, string>? itemNames)
    {
        var output = new List<EquipmentRecord>();
        foreach (int bag in new[] { 1, 2 })
        {
            int count = BagSlotCount(bytes, bag);
            for (int slot = 0; slot < count; slot++)
            {
                if (!BagRecordPresent(bytes, bag, slot)) continue;
                string id = EquipmentCatalogId(bytes, bag, slot);
                if (string.IsNullOrEmpty(id)) continue;
                string name = itemNames is not null && itemNames.TryGetValue(id, out string? mapped) ? mapped : "Unknown / Unnamed Equipment";
                int offset = bag == 1 ? WeaponManagerBase + slot * EquipRecordSize : AccessoryManagerBase + slot * EquipRecordSize;
                string category = bag == 1 ? "Weapon" : "Accessory";
                uint flags = offset >= 0 && offset + 18 <= bytes.Length ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 14, 4)) : 0;
                output.Add(new EquipmentRecord { BagIndex = bag, Slot = slot, Category = category, Id = id, Name = name, Flags = flags });
            }
        }
        return output;
    }

    public static bool EquipmentReplacementAllowed(string oldId, string newId, int bag)
    {
        if (bag == 1)
        {
            if (oldId.StartsWith("wea_ser_", StringComparison.Ordinal)) return newId.StartsWith("wea_ser_", StringComparison.Ordinal);
            if (oldId.StartsWith("wea_noe_", StringComparison.Ordinal) || oldId.StartsWith("xwea_noe_", StringComparison.Ordinal))
                return newId.StartsWith("wea_noe_", StringComparison.Ordinal) || newId.StartsWith("xwea_noe_", StringComparison.Ordinal);
            return newId.StartsWith("wea_", StringComparison.Ordinal) || newId.StartsWith("xwea_", StringComparison.Ordinal);
        }
        return bag == 2 && newId.StartsWith("acc_", StringComparison.Ordinal);
    }

    public static void WriteEquipmentCatalogId(byte[] bytes, int bag, int slot, string newId)
    {
        if (bag != 1 && bag != 2) throw new InvalidOperationException("Equipment bag must be Weapons or Accessories");
        if (slot < 0 || slot >= BagSlotCount(bytes, bag)) throw new InvalidOperationException("Invalid equipment slot");
        string oldId = EquipmentCatalogId(bytes, bag, slot);
        if (!EquipmentReplacementAllowed(oldId, newId, bag)) throw new InvalidOperationException($"Replacement {newId} is not compatible with {oldId}");
        byte[] encoded = Encoding.ASCII.GetBytes(newId);
        if (encoded.Length > 14) throw new InvalidDataException("Equipment catalog id exceeds 14 bytes");
        int offset = bag == 1 ? WeaponManagerBase + slot * EquipRecordSize : AccessoryManagerBase + slot * EquipRecordSize;
        CheckBounds(bytes, offset, 14);
        Array.Clear(bytes, offset, 14);
        encoded.CopyTo(bytes, offset);
    }

    public static byte[] WeaponBagHandle(int slot)
    {
        if (slot < 0 || slot > 75) throw new ArgumentOutOfRangeException(nameof(slot), $"Weapon slot {slot + 1} is outside verified addable range 1-76");
        byte[] output = Encoding.ASCII.GetBytes("0000000_000013\0");
        if (output.Length != 16) Array.Resize(ref output, 16);
        int value = 52 + slot;
        for (int i = 0; i < 7; i++) if ((value & (1 << (6 - i))) != 0) output[i] |= 0x80;
        return output;
    }

    public static bool IsNamedWeaponCatalogItem(ItemCatalog item)
        => item.BagIndex == 1 && !item.Name.StartsWith("unused / unnamed", StringComparison.OrdinalIgnoreCase)
           && (item.Id.StartsWith("wea_ser_", StringComparison.Ordinal) || item.Id.StartsWith("wea_noe_", StringComparison.Ordinal) || item.Id.StartsWith("xwea_noe_", StringComparison.Ordinal));

    public static string WeaponCharacter(string id)
        => id.StartsWith("wea_ser_", StringComparison.Ordinal) ? "Serah"
         : id.StartsWith("wea_noe_", StringComparison.Ordinal) || id.StartsWith("xwea_noe_", StringComparison.Ordinal) ? "Noel" : "";

    public static bool WeaponOwned(byte[] bytes, string id)
        => OwnedEquipment(bytes, null).Any(e => e.BagIndex == 1 && e.Id == id);

    public static int FindFreeWeaponSlot(byte[] bytes)
    {
        int limit = Math.Min(InventoryBags[1].Capacity, 76);
        for (int slot = 0; slot < limit; slot++)
        {
            int bagRecord = InventoryBags[1].Base + 4 + slot * 0x12;
            int manager = WeaponManagerBase + slot * EquipRecordSize;
            if (bagRecord + 18 > bytes.Length || manager + 18 > bytes.Length) break;
            bool bagEmpty = true;
            bool managerEmpty = true;
            for (int i = 0; i < 18; i++)
            {
                if (bytes[bagRecord + i] != 0) bagEmpty = false;
                if (bytes[manager + i] != 0) managerEmpty = false;
            }
            if (bagEmpty && managerEmpty) return slot;
        }
        return -1;
    }

    public static (byte[] Bytes, int Slot) AddMissingWeapon(byte[] source, ItemCatalog item)
    {
        if (!IsNamedWeaponCatalogItem(item)) throw new InvalidOperationException("Selected catalog entry is not a supported named Serah/Noel weapon");
        if (Encoding.ASCII.GetByteCount(item.Id) > 14) throw new InvalidDataException("Weapon catalog id exceeds 14 bytes");
        if (WeaponOwned(source, item.Id)) throw new InvalidOperationException($"{item.Name} is already owned");
        int slot = FindFreeWeaponSlot(source);
        if (slot < 0) throw new InvalidOperationException("No verified free weapon instance slot is available");
        byte[] handle = WeaponBagHandle(slot);
        byte[] work = (byte[])source.Clone();
        int bagRecord = InventoryBags[1].Base + 4 + slot * 0x12;
        int manager = WeaponManagerBase + slot * EquipRecordSize;
        CheckBounds(work, bagRecord, 18);
        CheckBounds(work, manager, 18);
        handle.CopyTo(work, bagRecord);
        BinaryPrimitives.WriteUInt16BigEndian(work.AsSpan(bagRecord + 16, 2), 1);
        if (slot >= BagSlotCount(work, 1)) BinaryPrimitives.WriteUInt32BigEndian(work.AsSpan(InventoryBags[1].Base, 4), (uint)(slot + 1));
        Array.Clear(work, manager, 18);
        Encoding.ASCII.GetBytes(item.Id).CopyTo(work, manager);
        work = RepairChecksum(work);
        if (!BagRecordPresent(work, 1, slot) || EquipmentCatalogId(work, 1, slot) != item.Id) throw new InvalidOperationException("New weapon instance failed internal verification");
        return (work, slot);
    }

    public static (byte[] Bytes, int Added) AddAllMissingWeapons(byte[] source, string character, IEnumerable<ItemCatalog> catalog)
    {
        if (character != "Serah" && character != "Noel") throw new ArgumentException("Character must be Serah or Noel", nameof(character));
        byte[] work = (byte[])source.Clone();
        int added = 0;
        foreach (ItemCatalog item in catalog)
        {
            if (!IsNamedWeaponCatalogItem(item) || WeaponCharacter(item.Id) != character || WeaponOwned(work, item.Id)) continue;
            var result = AddMissingWeapon(work, item);
            work = result.Bytes;
            added++;
        }
        return (RepairChecksum(work), added);
    }

    public static void VerifyEncryptedRoundtrip(byte[] encrypted, ulong ffKey, byte[] expectedPlain)
    {
        byte[] decrypted = DecryptBytes(encrypted, ffKey);
        byte[] expected = RepairChecksum(expectedPlain);
        if (decrypted.Length != expected.Length) throw new InvalidDataException("Verification size mismatch");
        if (StoredChecksum(decrypted) != CalculateChecksum(decrypted)) throw new InvalidDataException("Verification checksum failed");
        for (int i = 0; i < expected.Length; i++)
            if (decrypted[i] != expected[i]) throw new InvalidDataException($"Verification mismatch at 0x{i:X}");
    }

    public static void VerifyEncryptedFile(string path, ulong ffKey, byte[] expectedPlain)
        => VerifyEncryptedRoundtrip(File.ReadAllBytes(path), ffKey, expectedPlain);

    public static string ResolveSaveFolder(string path)
    {
        path = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Empty path");
        if (File.Exists(path)) path = Path.GetDirectoryName(Path.GetFullPath(path))!;
        else path = Path.GetFullPath(path);
        if (IsSaveFolder(path)) return path;

        List<string> matches = FindSaveFolders(path, 2).Take(9).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1) throw new InvalidDataException("Multiple PS3 save folders were found. Select the Final Fantasy XIII-2 save folder directly.");
        throw new InvalidDataException($"APP.DAT and KEY.DAT were not found together in {path}");
    }

    public static bool IsSaveFolder(string folder)
        => Directory.Exists(folder) && File.Exists(Path.Combine(folder, "APP.DAT")) && File.Exists(Path.Combine(folder, "KEY.DAT"));

    public static IEnumerable<string> FindSaveFolders(string root, int maxDepth)
    {
        var output = new List<string>();
        void Walk(string dir, int depth)
        {
            if (IsSaveFolder(dir)) { output.Add(dir); return; }
            if (depth >= maxDepth) return;
            try
            {
                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    if (Path.GetFileName(child).StartsWith('.')) continue;
                    Walk(child, depth + 1);
                    if (output.Count > 8) return;
                }
            }
            catch { }
        }
        Walk(root, 0);
        return output;
    }

    public static string MonsterRoleId(byte[] bytes, int slot)
    {
        int offset = MonsterBase + slot * MonsterStride - MonsterIdDelta + 0x2A;
        return offset < 0 || offset + 14 > bytes.Length ? "" : SafeAscii(bytes.AsSpan(offset, 14));
    }

    public static string MonsterModelId(byte[] bytes, int slot)
    {
        int offset = MonsterBase + slot * MonsterStride - MonsterIdDelta + 0x38;
        return offset < 0 || offset + 14 > bytes.Length ? "" : SafeAscii(bytes.AsSpan(offset, 14));
    }
}
