using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace FFXIII2SaveEditor;

public partial class MainWindow : Window
{
    private readonly GameData _data;
    private SaveLoadResult? _session;
    private byte[]? _plain;
    // Exact decrypted APP.DAT snapshot captured when the current save is loaded.
    // Restore commands copy only mapped stat fields from this immutable baseline.
    private byte[]? _loadedBaselinePlain;
    private bool _dirty;
    private bool _writeAllowed;
    private bool _refreshing;
    private bool _loading;

    private List<InventoryViewRow> _inventoryRows = new();
    private List<AdornmentViewRow> _adornmentRows = new();
    private readonly List<GuestPartyOption> _guestPartyOptions = new()
    {
        new GuestPartyOption { Name = "None", Status = "Safe", Note = "No story guest forced.", CanWrite = false },
        new GuestPartyOption { Name = "Snow (Story Guest)", Status = "Not mapped", Note = "Real guest-party slot is not verified in APP.DAT yet. Writer intentionally disabled.", CanWrite = false },
        new GuestPartyOption { Name = "Other Story Guest", Status = "Not mapped", Note = "Reserved for future verified guest entries.", CanWrite = false }
    };
    private List<EquipmentViewRow> _equipmentRows = new();
    private List<EquipmentCatalogViewRow> _equipmentCatalogRows = new();
    private List<CharacterAccessoryViewRow> _serahAccessoryRows = new();
    private List<CharacterAccessoryCatalogViewRow> _serahAccessoryCatalogRows = new();
    private List<CharacterAccessoryViewRow> _noelAccessoryRows = new();
    private List<CharacterAccessoryCatalogViewRow> _noelAccessoryCatalogRows = new();
    private List<MonsterRecord> _allMonsterRows = new();
    private List<MonsterRecord> _monsterRows = new();
    private List<MonsterLevelReferenceViewRow> _monsterLevelReferenceRows = new();
    private List<TraitViewRow> _availableTraitRows = new();
    private List<MonsterSkillViewRow> _currentSkillRows = new();
    private List<MonsterSkillViewRow> _availableSkillRows = new();
    private List<ChocoboRaceAbilityViewRow> _raceAbilityRows = new();
    private List<InfusionTransferItem> _infusionRows = new();
    private string[]? _copiedTraits;
    private string[]? _copiedSkills;
    private MonsterAbilityCountInfo? _abilityCountInfo;
    private readonly Stack<byte[]> _monsterUndo = new();
    private MonsterRecord? _selectedMonster;
    private readonly Dictionary<string, (string? Path, bool IsReal)> _monsterImageResolveCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _monsterImageManifestStampUtc = DateTime.MinValue;


    // Recommended SAFE direct-save passive sets. Automated presets use normal mapped passives only.
    // Existing awp_* slots are preserved and presets never inject new awp_* IDs because their
    // per-monster Red/Yellow Lock metadata is not mapped; awp_* itself is not a lock flag.
    private static readonly Dictionary<string, string[]> PassivePresets = new(StringComparer.OrdinalIgnoreCase)
    {
        // These are direct-save recommendations, not a simulation of the game's donor/rank rules.
        // Advanced awp_* role passives are intentionally excluded from automated presets.
        ["COM"] = new[]
        {
            "auto_hpp_9", "auto_att_8", "auto_defp_7", "auto_mdefp_7", "auto_rol",
            "auto_syncup", "auto_triplets", "auto_brav"
        },
        ["RAV"] = new[]
        {
            "auto_hpp_9", "auto_matt_8", "auto_defp_7", "auto_mdefp_7", "auto_rol",
            "auto_syncup", "auto_fait"
        },
        ["MED"] = new[]
        {
            "auto_hpp_9", "auto_matt_8", "auto_defp_7", "auto_mdefp_7", "auto_6def_5",
            "auto_rol", "auto_syncup", "auto_p_hast"
        },
        ["TANK"] = new[]
        {
            "auto_hpp_9", "auto_defp_7", "auto_mdefp_7", "auto_6def_5", "auto_def_s5",
            "auto_rol", "auto_prot", "auto_shel"
        },
        ["BALANCED"] = new[]
        {
            "auto_hpp_9", "auto_att_8", "auto_matt_8", "auto_defp_7", "auto_mdefp_7",
            "auto_6def_5", "auto_rol", "auto_syncup", "auto_hast", "auto_p_hast"
        }
    };

    public MainWindow()
    {
        InitializeComponent();
        _data = GameData.Load();
        RefreshMonsterLevelReference();
    }

    private void RefreshMonsterLevelReference()
    {
        string search = MonsterLevelReferenceSearchBox.Text.Trim();
        IEnumerable<MonsterLevelReferenceViewRow> query = _data.MonsterLevelCapCatalog.Select(cap =>
            new MonsterLevelReferenceViewRow
            {
                Name = cap.Name,
                Role = cap.Role,
                MaxLevel = cap.MaxLevel,
                Mapping = _data.MonsterNames.ContainsKey(cap.Id) ? "Mapped / enforced" : "Reference only",
                Id = cap.Id,
            });

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(row =>
                row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Role.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Mapping.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.MaxLevel.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        _monsterLevelReferenceRows = query
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
        MonsterLevelReferenceGrid.ItemsSource = null;
        MonsterLevelReferenceGrid.ItemsSource = _monsterLevelReferenceRows;

        int uniqueNames = _data.MonsterLevelCapCatalog
            .Select(cap => cap.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        MonsterLevelReferenceCountText.Text =
            $"{_monsterLevelReferenceRows.Count} / {_data.MonsterLevelCapCatalog.Count} variants • {uniqueNames} names";
    }

    private void MonsterLevelReferenceSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_data is null) return;
        RefreshMonsterLevelReference();
    }

    private byte[] Plain => _plain ?? throw new InvalidOperationException("No save is loaded.");

    private bool AppIsPfdProtected
        => _session?.PfdNames.Any(n => n.Equals("APP.DAT", StringComparison.OrdinalIgnoreCase)) == true;

    private async void OpenSave_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string? folder = ChooseSaveFolder(
            "Select APP.DAT from the Final Fantasy XIII-2 PS3 save folder",
            SaveFolderText.Text);
        if (folder is null) return;
        SaveFolderText.Text = folder;
        await LoadFolderAsync(folder);
    }

    private async void LoadSave_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await LoadFolderAsync(SaveFolderText.Text);
    }

    private async Task LoadFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Warn("Choose the PS3 save folder first.");
            return;
        }
        if (!ConfirmDiscardChanges("load another save")) return;

        // Loading is transactional. If decryption or the first UI refresh fails, restore the exact
        // previously active session so the path shown on screen can never disagree with the save
        // that Save + Backup would write.
        SaveLoadResult? previousSession = _session;
        byte[]? previousPlain = _plain;
        byte[]? previousBaselinePlain = _loadedBaselinePlain is null ? null : (byte[])_loadedBaselinePlain.Clone();
        bool previousDirty = _dirty;
        bool previousWriteAllowed = _writeAllowed;
        MonsterRecord? previousSelectedMonster = _selectedMonster;
        string[]? previousCopiedTraits = _copiedTraits is null ? null : (string[])_copiedTraits.Clone();
        string[]? previousCopiedSkills = _copiedSkills is null ? null : (string[])_copiedSkills.Clone();
        byte[][] previousUndo = _monsterUndo.ToArray().Select(x => (byte[])x.Clone()).ToArray();

        SetLoading(true);
        try
        {
            SaveLoadResult result = await Task.Run(() => SaveCore.LoadSaveFolder(path));
            _session = result;
            _plain = result.Plain;
            _loadedBaselinePlain = (byte[])result.Plain.Clone();
            _dirty = false;
            _selectedMonster = null;
            _copiedTraits = null;
            _copiedSkills = null;
            _abilityCountInfo = null;
            _monsterUndo.Clear();
            ClearMonsterImageCache();
            SaveFolderText.Text = result.Folder;

            RegionSupport support = SaveCore.RegionSupportForTitle(result.TitleId);
            _writeAllowed = support == RegionSupport.Tested && result.WorkingKey != 0 && result.PfdMetadataReliable;
            UpdateSaveInfo();
            RefreshSelectedTab();
            Status($"Save loaded successfully. {result.Mode}");
            if (!result.PfdMetadataReliable)
                Warn("PARAM.PFD exists but could not be parsed reliably. The save was opened in read-only safety mode because APP.DAT/KEY.DAT protection state cannot be trusted.");
        }
        catch (Exception ex)
        {
            _session = previousSession;
            _plain = previousPlain;
            _loadedBaselinePlain = previousBaselinePlain;
            _dirty = previousDirty;
            _writeAllowed = previousWriteAllowed;
            _selectedMonster = previousSelectedMonster;
            _copiedTraits = previousCopiedTraits;
            _copiedSkills = previousCopiedSkills;
            _abilityCountInfo = null;
            _monsterUndo.Clear();
            // Stack.ToArray() is newest -> oldest. Push oldest -> newest to reconstruct it.
            for (int i = previousUndo.Length - 1; i >= 0; i--) _monsterUndo.Push(previousUndo[i]);
            SaveFolderText.Text = previousSession?.Folder ?? path;
            UpdateSaveInfo();
            if (previousPlain is not null)
            {
                try { RefreshSelectedTab(); } catch { }
            }
            Error(ex.Message, "Load failed");
            Status(previousPlain is null ? "Load failed. No save is active." : "Load failed. The previously loaded save remains active.");
        }
        finally
        {
            SetLoading(false);
            UpdateActionButtons();
        }
    }

    
    private bool IsAdvancedInventoryMode()
    {
        if (cmbInventoryMode?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag)
            return string.Equals(tag, "advanced", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void InventoryMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (IsAdvancedInventoryMode() && _plain is not null)
        {
            bool accepted = Confirm(
                "Advanced Mode exposes Key Items / OOPArts and Internal / Special records.\n\n" +
                "These fields can create impossible progression states. Max Safe Inventory will still exclude them.\n\n" +
                "Enable Advanced Mode?");
            if (!accepted)
            {
                cmbInventoryMode.SelectedIndex = 0;
                return;
            }
        }

        if (txtInventoryModeHint is not null)
        {
            txtInventoryModeHint.Text = IsAdvancedInventoryMode()
                ? "Advanced: Key Items / OOPArts + Internal / Special are visible. Bulk max remains safe-only."
                : "Simple: ordinary stackable inventory only (recommended).";
        }

        RefreshInventoryViewForMode();
    }

    private void RefreshInventoryViewForMode()
    {
        // Reuse the normal inventory refresh path if a save is already loaded.
        try
        {
            RefreshInventory();
        }
        catch
        {
            // The selection event can fire during XAML construction before the save/UI is ready.
        }
    }

private void SetLoading(bool loading)
    {
        _loading = loading;
        OpenSaveButton.IsEnabled = !loading;
        LoadSaveButton.IsEnabled = !loading;
        LoadSaveButton.Content = loading ? "Loading..." : "Load Save";
        SaveFolderText.IsEnabled = !loading;
        MainTabs.IsEnabled = !loading;
        UpdateActionButtons();
        if (loading) StatusText.Text = "Loading save...";
    }

    private void UpdateSaveInfo()
    {
        if (_session is null || _plain is null)
        {
            SaveTypeText.Text = "Not loaded";
            RegionText.Text = "-";
            ChecksumText.Text = "-";
            AccessText.Text = "-";
            LoadedText.Text = "-";
            return;
        }

        SaveTypeText.Text = "Normal Save";
        string region = _session.TitleId switch
        {
            "BLES01269" => "BLES01269 (EUR)",
            "BLUS30776" => "BLUS30776 (USA)",
            "BCAS20224" => "BCAS20224 (Asia)",
            "" => "Unknown",
            _ => _session.TitleId
        };
        RegionText.Text = region;
        ChecksumText.Text = SaveCore.StoredChecksum(_plain) == SaveCore.CalculateChecksum(_plain) ? "Valid" : "Invalid - repair on edit";
        AccessText.Text = _writeAllowed
            ? (AppIsPfdProtected ? "Writable / export + resign required" : "Writable / tested")
            : (_session.PfdMetadataReliable ? "Read-only safety mode" : "Read-only / PFD unreadable");
        LoadedText.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void UpdateActionButtons()
    {
        bool loaded = _plain is not null;
        ExportDecryptedButton.IsEnabled = loaded && !_loading;
        SaveEncryptedButton.IsEnabled = loaded && _writeAllowed && !_loading;
        SaveInPlaceButton.IsEnabled = loaded && _writeAllowed && !_loading && !AppIsPfdProtected;
        SaveInPlaceButton.Content = AppIsPfdProtected ? "In-Place Disabled (PFD)" : "Save In Place + Backup";
        bool chocoboEditable = loaded && _writeAllowed && !_loading
            && _selectedMonster is not null
            && SaveCore.IsChocoboMonster(_selectedMonster.Id, _selectedMonster.Name);
        MaxChocoboRaceButton.IsEnabled = chocoboEditable;
        GodChocoboButton.IsEnabled = chocoboEditable;
        ApplyRaceRpButton.IsEnabled = loaded && _writeAllowed && !_loading;
        UpdateRaceAbilityButtons();
    }

    private bool RequireLoaded()
    {
        if (_loading) { Warn("Please wait for the current load operation to finish."); return false; }
        if (_plain is not null && _session is not null) return true;
        Warn("Load a save first.");
        return false;
    }

    private bool RequireEditable()
    {
        if (!RequireLoaded()) return false;
        if (_writeAllowed) return true;
        Warn("This save is open in read-only safety mode. Only BLES01269 (EUR) with a trusted encryption key is enabled for writing in this build.");
        return false;
    }

    private void MarkDirty(string message)
    {
        _plain = SaveCore.RepairChecksum(Plain);
        _dirty = true;
        UpdateSaveInfo();
        Status(AppIsPfdProtected
            ? message + " - changes are in memory. Use Encrypted Copy, then complete your normal PS3 resign/rebuild step."
            : message + " - changes are in memory until you save.");
    }

    private void RefreshSelectedTab()
    {
        if (_plain is null) return;
        _refreshing = true;
        try
        {
            switch (MainTabs.SelectedIndex)
            {
                case 0: RefreshGeneral(); break;
                case 1: RefreshCharacters(); break;
                case 2: RefreshInventory(); break;
                case 3: RefreshAdornments(); break;
                case 4: RefreshPartyGuests(); break;
                case 5: RefreshEquipment(); break;
                case 6: RefreshMonsters(); break;
                case 7: RefreshFragments(); break;
                case 8: RefreshCasinoRecords(); break;
            }
        }
        finally { _refreshing = false; }
    }

    private void RefreshGeneral()
    {
        GilBox.Text = SaveCore.ReadValue(Plain, SaveCore.GilOffset, ValueKind.BE32).ToString(CultureInfo.InvariantCulture);
        CoinsBox.Text = SaveCore.ReadValue(Plain, SaveCore.CasinoCoinsOffset, ValueKind.BE32).ToString(CultureInfo.InvariantCulture);
        SerahCpGeneralBox.Text = SaveCore.ReadValue(Plain, SaveCore.Characters["Serah"]["CP"].Offset, ValueKind.BE32).ToString(CultureInfo.InvariantCulture);
        NoelCpGeneralBox.Text = SaveCore.ReadValue(Plain, SaveCore.Characters["Noel"]["CP"].Offset, ValueKind.BE32).ToString(CultureInfo.InvariantCulture);

        var top = SaveCore.IterInventory(Plain, _data.ItemNames, _data.MonsterNames)
            .Where(x => x.Qty > 0 && x.Name != "Unknown / Unnamed Item")
            .OrderByDescending(x => x.Qty)
            .ThenBy(x => x.Name)
            .Take(10)
            .Select(x => new { x.Name, Quantity = x.Qty })
            .ToList();
        QuickItemsGrid.ItemsSource = top;

        int fragments = _session?.FragmentCount ?? -1;
        FragmentsProgressText.Text = fragments >= 0
            ? $"{fragments} / {SaveCore.FragmentCount}"
            : $"? / {SaveCore.FragmentCount}";
        MonstersProgressText.Text = SaveCore.ActiveMonsters(Plain, _data.MonsterNames, _data.MonsterLevelCaps).Count.ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyGeneral_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        try
        {
            uint gil = ParseBox(GilBox, uint.MaxValue, "Gil");
            uint coins = ParseBox(CoinsBox, uint.MaxValue, "Casino Coins");
            FieldSpec serahCp = SaveCore.Characters["Serah"]["CP"];
            FieldSpec noelCp = SaveCore.Characters["Noel"]["CP"];
            uint serah = ParseBox(SerahCpGeneralBox, serahCp.Max, "Serah CP");
            uint noel = ParseBox(NoelCpGeneralBox, noelCp.Max, "Noel CP");
            _plain = SaveCore.ApplyFieldTransaction(Plain, new[]
            {
                new FieldWrite(SaveCore.GilOffset, ValueKind.BE32, gil, uint.MaxValue),
                new FieldWrite(SaveCore.CasinoCoinsOffset, ValueKind.BE32, coins, uint.MaxValue),
                new FieldWrite(serahCp.Offset, serahCp.Kind, serah, serahCp.Max),
                new FieldWrite(noelCp.Offset, noelCp.Kind, noel, noelCp.Max),
            });
            MarkDirty("General values updated");
            RefreshGeneral();
            RefreshCharacters();
        }
        catch (Exception ex) { Error(ex.Message, "Invalid value"); }
    }

    private void RefreshCasinoRecords()
    {
        PokerGamesPlayedBox.Text = SaveCore.ReadValue(Plain, SaveCore.PokerGamesPlayedOffset, ValueKind.BE16).ToString(CultureInfo.InvariantCulture);
        PokerGamesWonBox.Text = SaveCore.ReadValue(Plain, SaveCore.PokerGamesWonOffset, ValueKind.BE16).ToString(CultureInfo.InvariantCulture);
        PokerMatchesWonBox.Text = SaveCore.ReadValue(Plain, SaveCore.PokerMatchesWonOffset, ValueKind.BE16).ToString(CultureInfo.InvariantCulture);
        ChronobindGamesPlayedBox.Text = SaveCore.ReadValue(Plain, SaveCore.ChronobindGamesPlayedOffset, ValueKind.BE16).ToString(CultureInfo.InvariantCulture);
        ChronobindGamesWonBox.Text = SaveCore.ReadValue(Plain, SaveCore.ChronobindGamesWonOffset, ValueKind.BE16).ToString(CultureInfo.InvariantCulture);
        ChronobindMatchesWonBox.Text = SaveCore.ReadValue(Plain, SaveCore.ChronobindMatchesWonOffset, ValueKind.BE16).ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyCasinoRecords_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        try
        {
            uint pokerPlayed = ParseBox(PokerGamesPlayedBox, SaveCore.CasinoRecordMax, "Poker Games Played");
            uint pokerWon = ParseBox(PokerGamesWonBox, SaveCore.CasinoRecordMax, "Poker Games Won");
            uint pokerMatches = ParseBox(PokerMatchesWonBox, SaveCore.CasinoRecordMax, "Poker Matches Won");
            uint chronoPlayed = ParseBox(ChronobindGamesPlayedBox, SaveCore.CasinoRecordMax, "Chronobind Games Played");
            uint chronoWon = ParseBox(ChronobindGamesWonBox, SaveCore.CasinoRecordMax, "Chronobind Games Won");
            uint chronoMatches = ParseBox(ChronobindMatchesWonBox, SaveCore.CasinoRecordMax, "Chronobind Matches Won");

            _plain = SaveCore.ApplyFieldTransaction(Plain, new[]
            {
                new FieldWrite(SaveCore.PokerGamesPlayedOffset, ValueKind.BE16, pokerPlayed, SaveCore.CasinoRecordMax),
                new FieldWrite(SaveCore.PokerGamesWonOffset, ValueKind.BE16, pokerWon, SaveCore.CasinoRecordMax),
                new FieldWrite(SaveCore.PokerMatchesWonOffset, ValueKind.BE16, pokerMatches, SaveCore.CasinoRecordMax),
                new FieldWrite(SaveCore.ChronobindGamesPlayedOffset, ValueKind.BE16, chronoPlayed, SaveCore.CasinoRecordMax),
                new FieldWrite(SaveCore.ChronobindGamesWonOffset, ValueKind.BE16, chronoWon, SaveCore.CasinoRecordMax),
                new FieldWrite(SaveCore.ChronobindMatchesWonOffset, ValueKind.BE16, chronoMatches, SaveCore.CasinoRecordMax),
            });
            MarkDirty("Casino records updated");
            RefreshCasinoRecords();
        }
        catch (Exception ex) { Error(ex.Message, "Invalid casino record"); }
    }

    private Dictionary<string, WpfTextBox> CharacterBoxes(string character) => character switch
    {
        "Serah" => new(StringComparer.OrdinalIgnoreCase)
        {
            ["CP"] = SerahCP, ["HP"] = SerahHP, ["Strength"] = SerahStrength,
            ["Magic"] = SerahMagic, ["ATB"] = SerahATB, ["Equipment Capacity"] = SerahCapacity
        },
        "Noel" => new(StringComparer.OrdinalIgnoreCase)
        {
            ["CP"] = NoelCP, ["HP"] = NoelHP, ["Strength"] = NoelStrength,
            ["Magic"] = NoelMagic, ["ATB"] = NoelATB, ["Equipment Capacity"] = NoelCapacity
        },
        _ => new(StringComparer.OrdinalIgnoreCase)
        {
            ["CP"] = LightningCP, ["HP"] = LightningHP, ["Strength"] = LightningStrength,
            ["Magic"] = LightningMagic, ["ATB"] = LightningATB
        },
    };


    private Dictionary<string, WpfTextBox> CharacterRoleBoxes(string character) => character switch
    {
        "Serah" => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Commando"] = SerahRoleCOM,
            ["Ravager"] = SerahRoleRAV,
            ["Sentinel"] = SerahRoleSEN,
            ["Saboteur"] = SerahRoleSAB,
            ["Synergist"] = SerahRoleSYN,
            ["Medic"] = SerahRoleMED,
        },
        "Noel" => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Commando"] = NoelRoleCOM,
            ["Ravager"] = NoelRoleRAV,
            ["Sentinel"] = NoelRoleSEN,
            ["Saboteur"] = NoelRoleSAB,
            ["Synergist"] = NoelRoleSYN,
            ["Medic"] = NoelRoleMED,
        },
        _ => throw new ArgumentException("Role-level editor supports Serah or Noel only.", nameof(character))
    };

    private TextBlock CharacterRoleStatusText(string character)
        => character.Equals("Serah", StringComparison.OrdinalIgnoreCase) ? SerahRoleStatusText : NoelRoleStatusText;

    private void RefreshCharacterRoleLevels(string character)
    {
        if (_plain is null) return;
        Dictionary<string, WpfTextBox> boxes = CharacterRoleBoxes(character);
        Dictionary<string, FieldSpec> specs = SaveCore.CharacterRoleLevels[character];

        foreach (string role in SaveCore.CharacterRoleOrder)
        {
            FieldSpec spec = specs[role];
            boxes[role].Text = SaveCore.ReadValue(Plain, spec.Offset, spec.Kind)
                .ToString(CultureInfo.InvariantCulture);
        }

        string summary = string.Join("  •  ", new[]
        {
            $"COM {boxes["Commando"].Text}",
            $"RAV {boxes["Ravager"].Text}",
            $"SEN {boxes["Sentinel"].Text}",
            $"SAB {boxes["Saboteur"].Text}",
            $"SYN {boxes["Synergist"].Text}",
            $"MED {boxes["Medic"].Text}",
        });
        CharacterRoleStatusText(character).Text = summary;
    }

    private void ApplyCharacterRoleLevels(string character, bool max)
    {
        if (!RequireEditable()) return;

        try
        {
            Dictionary<string, WpfTextBox> boxes = CharacterRoleBoxes(character);
            Dictionary<string, FieldSpec> specs = SaveCore.CharacterRoleLevels[character];
            var writes = new List<FieldWrite>();

            foreach (string role in SaveCore.CharacterRoleOrder)
            {
                FieldSpec spec = specs[role];
                uint value = max
                    ? SaveCore.CharacterRoleLevelMax
                    : ParseBox(boxes[role], SaveCore.CharacterRoleLevelMax, $"{character} {role} level");
                writes.Add(new FieldWrite(spec.Offset, spec.Kind, value, SaveCore.CharacterRoleLevelMax));
            }

            if (max && !Confirm($"Set all six of {character}'s paradigm role levels to 99?\n\nThis changes role-level counters only. It does not synthesize missing Crystarium nodes, abilities, or stat gains."))
                return;

            _plain = SaveCore.ApplyFieldTransaction(Plain, writes);
            MarkDirty($"{character} paradigm role levels updated");
            RefreshCharacterRoleLevels(character);
            RefreshGeneral();
            Status($"{character} role levels saved.");
        }
        catch (Exception ex)
        {
            Error(ex.Message, $"{character} role-level edit failed");
        }
    }

    private int RestoreCharacterRoleLevelsFromLoadedBaseline(string character, bool markDirty = true)
    {
        if (_loadedBaselinePlain is null) return 0;

        byte[] work = (byte[])Plain.Clone();
        int changed = 0;
        foreach (FieldSpec spec in SaveCore.CharacterRoleLevels[character].Values)
        {
            uint original = SaveCore.ReadValue(_loadedBaselinePlain, spec.Offset, spec.Kind);
            uint current = SaveCore.ReadValue(work, spec.Offset, spec.Kind);
            if (original == current) continue;
            SaveCore.WriteValue(work, spec.Offset, spec.Kind, original);
            changed++;
        }

        if (changed > 0)
        {
            _plain = SaveCore.RepairChecksum(work);
            if (markDirty)
                MarkDirty($"{character} paradigm role levels restored to loaded values");
        }
        return changed;
    }

    private void RestoreCharacterRoleLevels(string character)
    {
        if (!RequireLoadedBaseline()) return;
        if (!Confirm($"Restore all six of {character}'s paradigm role levels to the exact values captured when this save was loaded?"))
            return;

        int changed = RestoreCharacterRoleLevelsFromLoadedBaseline(character);
        RefreshCharacterRoleLevels(character);
        RefreshGeneral();
        Status(changed == 0
            ? $"{character}'s role levels already match the loaded save."
            : $"{character}: restored {changed} role level field(s).");
    }

    private void ApplySerahRoles_Click(object sender, RoutedEventArgs e) => ApplyCharacterRoleLevels("Serah", false);
    private void MaxSerahRoles_Click(object sender, RoutedEventArgs e) => ApplyCharacterRoleLevels("Serah", true);
    private void RestoreSerahRoles_Click(object sender, RoutedEventArgs e) => RestoreCharacterRoleLevels("Serah");

    private void ApplyNoelRoles_Click(object sender, RoutedEventArgs e) => ApplyCharacterRoleLevels("Noel", false);
    private void MaxNoelRoles_Click(object sender, RoutedEventArgs e) => ApplyCharacterRoleLevels("Noel", true);
    private void RestoreNoelRoles_Click(object sender, RoutedEventArgs e) => RestoreCharacterRoleLevels("Noel");

    private void RefreshCharacters()
    {
        foreach (string character in new[] { "Serah", "Noel", "Lightning DLC" })
        {
            var boxes = CharacterBoxes(character);
            foreach (var pair in boxes)
            {
                FieldSpec spec = SaveCore.Characters[character][pair.Key];
                pair.Value.Text = SaveCore.ReadValue(Plain, spec.Offset, spec.Kind).ToString(CultureInfo.InvariantCulture);
            }
        }
        RefreshCharacterRoleLevels("Serah");
        RefreshCharacterRoleLevels("Noel");
        RefreshCharacterAccessories("Serah");
        RefreshCharacterAccessories("Noel");
    }

    private DataGrid CharacterEquippedEffectsGrid(string character) => character == "Serah" ? SerahEquippedEffectsGrid : NoelEquippedEffectsGrid;
    private DataGrid CharacterAccessoryCatalogGrid(string character) => character == "Serah" ? SerahAccessoryCatalogGrid : NoelAccessoryCatalogGrid;
    private WpfTextBox CharacterAccessorySearchBox(string character) => character == "Serah" ? SerahAccessorySearchBox : NoelAccessorySearchBox;
    private TextBlock CharacterAccessoryStatusText(string character) => character == "Serah" ? SerahAccessoryStatusText : NoelAccessoryStatusText;

    private void RefreshCharacterAccessories(string character)
    {
        if (_plain is null) return;
        DataGrid equippedGrid = CharacterEquippedEffectsGrid(character);
        int previousSlot = equippedGrid.SelectedItem is CharacterAccessoryViewRow old ? old.ManagerSlot : -1;
        List<(int EquipPosition, int ManagerSlot)> references;
        try { references = SaveCore.CharacterEquippedAccessoryReferences(Plain, character); }
        catch (Exception ex)
        {
            equippedGrid.ItemsSource = Array.Empty<CharacterAccessoryViewRow>();
            CharacterAccessoryCatalogGrid(character).ItemsSource = Array.Empty<CharacterAccessoryCatalogViewRow>();
            CharacterAccessoryStatusText(character).Text = "Equipment references could not be verified: " + ex.Message;
            return;
        }

        var rows = new List<CharacterAccessoryViewRow>();
        foreach (var reference in references)
        {
            int managerSlot = reference.ManagerSlot;
            string id = SaveCore.EquipmentCatalogId(Plain, 2, managerSlot);
            _data.AccessoryEffectsById.TryGetValue(id, out AccessoryEffect? effect);
            string name = effect?.Name ?? (_data.ItemNames.TryGetValue(id, out string? mapped) ? mapped : id);
            rows.Add(new CharacterAccessoryViewRow
            {
                EquipPosition = reference.EquipPosition,
                ManagerSlot = managerSlot,
                Name = name,
                Effect = effect?.Effect ?? "Effect/capacity not mapped in this safe catalog",
                Capacity = effect?.Capacity ?? -1,
                EffectInfo = effect,
            });
        }

        if (character == "Serah") _serahAccessoryRows = rows; else _noelAccessoryRows = rows;
        equippedGrid.ItemsSource = rows;
        int restore = rows.FindIndex(r => r.ManagerSlot == previousSlot);
        equippedGrid.SelectedIndex = restore >= 0 ? restore : rows.Count > 0 ? 0 : -1;
        RefreshCharacterAccessoryCatalog(character);
    }

    private bool IsCharacterAccessoryReplacementSafe(string character, CharacterAccessoryViewRow current, AccessoryEffect candidate, out string reason)
    {
        List<int> slots;
        try
        {
            slots = SaveCore.CharacterEquippedAccessorySlots(Plain, character);
            if (!slots.Contains(current.ManagerSlot))
            {
                reason = "Blocked: selected accessory instance is no longer equipped by this character";
                return false;
            }
        }
        catch (Exception ex) { reason = "Blocked: " + ex.Message; return false; }

        string currentId = SaveCore.EquipmentCatalogId(Plain, 2, current.ManagerSlot);
        int oldCapacity = _data.AccessoryEffectsById.TryGetValue(currentId, out AccessoryEffect? currentInfo) ? currentInfo.Capacity : -1;
        if (oldCapacity < 0)
        {
            reason = "Unverified: currently equipped accessory capacity is not mapped";
            return false;
        }

        if (candidate.Capacity < 0)
        {
            reason = "Unverified: selected accessory capacity/effect is not mapped";
            return false;
        }

        int total = 0;
        bool totalKnown = true;
        foreach (int slot in slots)
        {
            string id = SaveCore.EquipmentCatalogId(Plain, 2, slot);
            if (!_data.AccessoryEffectsById.TryGetValue(id, out AccessoryEffect? info) || info.Capacity < 0)
            {
                totalKnown = false;
                break;
            }
            total += info.Capacity;
        }

        if (!totalKnown)
        {
            if (candidate.Capacity <= oldCapacity)
            {
                reason = $"Safe non-increasing replacement: capacity {oldCapacity} -> {candidate.Capacity}; full equipped total cannot be verified because another accessory is not mapped";
                return true;
            }
            reason = "Blocked: capacity increase requires every equipped accessory capacity to be mapped";
            return false;
        }

        int nextTotal = total - oldCapacity + candidate.Capacity;
        FieldSpec capacitySpec = SaveCore.Characters[character]["Equipment Capacity"];
        int storedLimit = checked((int)SaveCore.ReadValue(Plain, capacitySpec.Offset, capacitySpec.Kind));
        int limit = Math.Min(storedLimit, SaveCore.CharacterAccessoryCapacityMax);
        if (nextTotal <= limit)
        {
            reason = storedLimit > SaveCore.CharacterAccessoryCapacityMax
                ? $"Safe: total capacity {nextTotal}/{limit} (stored value {storedLimit} exceeds the normal game maximum and is not used for this safety check)"
                : $"Safe: total capacity {nextTotal}/{limit}";
            return true;
        }
        reason = storedLimit > SaveCore.CharacterAccessoryCapacityMax
            ? $"Over capacity: {nextTotal}/{limit} (stored value {storedLimit} exceeds the normal game maximum)"
            : $"Over capacity: {nextTotal}/{limit}";
        return false;
    }

    private void RefreshCharacterAccessoryCatalog(string character)
    {
        if (_plain is null) return;
        DataGrid equippedGrid = CharacterEquippedEffectsGrid(character);
        DataGrid catalogGrid = CharacterAccessoryCatalogGrid(character);
        string search = CharacterAccessorySearchBox(character).Text.Trim();
        if (equippedGrid.SelectedItem is not CharacterAccessoryViewRow current)
        {
            catalogGrid.ItemsSource = Array.Empty<CharacterAccessoryCatalogViewRow>();
            CharacterAccessoryStatusText(character).Text = "No equipped accessory slot is available to transform.";
            return;
        }

        var rows = _data.AccessoryEffects
            .Where(a => Matches(search, a.Name, a.Effect, a.Category))
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Name)
            .Select(a =>
            {
                bool safe = IsCharacterAccessoryReplacementSafe(character, current, a, out string reason);
                return new CharacterAccessoryCatalogViewRow
                {
                    Name = a.Name,
                    Effect = a.Effect,
                    Category = a.Category,
                    Capacity = a.Capacity,
                    Safety = safe
                        ? "Safe"
                        : reason.StartsWith("Over capacity", StringComparison.Ordinal)
                            ? "Over capacity"
                            : reason.StartsWith("Unverified:", StringComparison.Ordinal)
                                ? "Unverified"
                                : "Blocked",
                    EffectInfo = a,
                };
            })
            .ToList();

        if (character == "Serah") _serahAccessoryCatalogRows = rows; else _noelAccessoryCatalogRows = rows;
        catalogGrid.ItemsSource = rows;
        CharacterAccessoryStatusText(character).Text =
            $"Selected slot: {current.Name} — {current.Effect}. Replacement changes this equipped accessory instance; character equipment references stay intact.";
    }

    private void CharacterAccessorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is null || _refreshing || sender is not FrameworkElement fe || fe.Tag is not string character) return;
        RefreshCharacterAccessoryCatalog(character);
    }

    private void CharacterAccessoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_plain is null || _refreshing || sender is not FrameworkElement fe || fe.Tag is not string character) return;
        RefreshCharacterAccessoryCatalog(character);
    }

    private void ReplaceCharacterAccessory_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string character) return;
        if (CharacterEquippedEffectsGrid(character).SelectedItem is not CharacterAccessoryViewRow current)
        {
            Warn("Select an equipped accessory/passive slot first.");
            return;
        }
        if (CharacterAccessoryCatalogGrid(character).SelectedItem is not CharacterAccessoryCatalogViewRow selected || selected.EffectInfo is null)
        {
            Warn("Select a passive/resistance accessory from the catalog first.");
            return;
        }
        string liveId = SaveCore.EquipmentCatalogId(Plain, 2, current.ManagerSlot);
        if (current.EffectInfo is not null && !string.Equals(liveId, current.EffectInfo.Id, StringComparison.OrdinalIgnoreCase))
        {
            RefreshCharacterAccessories(character);
            Warn("That equipped accessory changed since this row was selected. The list has been refreshed; select it again before applying a passive/resistance change.");
            return;
        }
        bool verifiedSafe = IsCharacterAccessoryReplacementSafe(character, current, selected.EffectInfo, out string reason);
        bool unverified = !verifiedSafe && reason.StartsWith("Unverified:", StringComparison.Ordinal);
        if (!verifiedSafe && !unverified)
        {
            Warn(reason + ". Choose a lower-capacity option or increase the character's verified Equipment Capacity first.");
            return;
        }

        string verificationWarning = unverified
            ? "\n\nWARNING: This accessory ID is valid, but its capacity/effect metadata is not yet mapped in the editor. The save reference can be changed, but accessory-capacity safety cannot be guaranteed for this choice."
            : "";
        string message = $"Replace {character}'s equipped {current.Name} ({current.Effect}) with {selected.Name} ({selected.Effect})?\n\n{reason}{verificationWarning}\n\nThis transforms the existing equipped accessory instance; it does not create a direct character passive slot. Accessory synthesis-group bonus abilities can also change when the accessory ID changes.";
        if (MessageBox.Show(message, "Confirm passive / resistance replacement", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _plain = SaveCore.ReplaceCharacterAccessory(Plain, character, current.ManagerSlot, selected.EffectInfo.Id);
            MarkDirty($"{character} passive/resistance changed to {selected.Effect}");
            RefreshCharacterAccessories(character);
            RefreshEquipment();
        }
        catch (Exception ex) { Error(ex.Message, "Character passive/resistance edit failed"); }
    }

    private void ApplyCharacter(string character, bool max)
    {
        if (!RequireEditable()) return;
        try
        {
            var boxes = CharacterBoxes(character);
            var writes = new List<FieldWrite>();
            foreach (var pair in boxes)
            {
                FieldSpec spec = SaveCore.Characters[character][pair.Key];
                uint value = max ? spec.Max : ParseBox(pair.Value, spec.Max, $"{character} {pair.Key}");
                writes.Add(new FieldWrite(spec.Offset, spec.Kind, value, spec.Max));
            }
            _plain = SaveCore.ApplyFieldTransaction(Plain, writes);
            MarkDirty(character + " stats updated");
            RefreshCharacters();
            RefreshGeneral();
        }
        catch (Exception ex) { Error(ex.Message, "Character edit failed"); }
    }


    private bool RequireLoadedBaseline()
    {
        if (!RequireEditable()) return false;
        if (_loadedBaselinePlain is not null && _loadedBaselinePlain.Length == Plain.Length) return true;
        Warn("The original loaded-stat snapshot is unavailable. Reload the save to create a fresh baseline.");
        return false;
    }

    private int RestoreCharacterFromLoadedBaseline(string character, bool markDirty = true)
    {
        if (_loadedBaselinePlain is null) return 0;
        byte[] work = (byte[])Plain.Clone();
        int changed = 0;

        foreach (FieldSpec spec in SaveCore.Characters[character].Values)
        {
            uint original = SaveCore.ReadValue(_loadedBaselinePlain, spec.Offset, spec.Kind);
            uint current = SaveCore.ReadValue(work, spec.Offset, spec.Kind);
            if (current == original) continue;
            SaveCore.WriteValue(work, spec.Offset, spec.Kind, original);
            changed++;
        }

        if (character.Equals("Serah", StringComparison.OrdinalIgnoreCase) ||
            character.Equals("Noel", StringComparison.OrdinalIgnoreCase))
        {
            foreach (FieldSpec spec in SaveCore.CharacterRoleLevels[character].Values)
            {
                uint original = SaveCore.ReadValue(_loadedBaselinePlain, spec.Offset, spec.Kind);
                uint current = SaveCore.ReadValue(work, spec.Offset, spec.Kind);
                if (current == original) continue;
                SaveCore.WriteValue(work, spec.Offset, spec.Kind, original);
                changed++;
            }
        }

        if (changed > 0)
        {
            _plain = SaveCore.RepairChecksum(work);
            if (markDirty)
                MarkDirty($"{character} mapped stats and role levels restored to loaded values");
        }
        return changed;
    }

    private void RestoreSerah_Click(object sender, RoutedEventArgs e) => RestoreCharacterWithConfirmation("Serah");
    private void RestoreNoel_Click(object sender, RoutedEventArgs e) => RestoreCharacterWithConfirmation("Noel");

    private void RestoreCharacterWithConfirmation(string character)
    {
        if (!RequireLoadedBaseline()) return;
        if (!Confirm($"Restore {character}'s mapped CP, HP, Strength, Magic, ATB, Equipment Capacity and six paradigm role levels to the exact values from when this save was loaded?\n\nEquipment, abilities, inventory and story data are not changed.")) return;

        int changed = RestoreCharacterFromLoadedBaseline(character);
        RefreshCharacters();
        RefreshGeneral();
        Status(changed == 0
            ? $"{character}'s mapped stats already match the loaded save."
            : $"{character}: restored {changed} mapped stat field(s) to loaded values.");
    }

    private void ApplySerah_Click(object sender, RoutedEventArgs e) => ApplyCharacter("Serah", false);
    private void MaxSerah_Click(object sender, RoutedEventArgs e) => ApplyCharacter("Serah", true);
    private void ApplyNoel_Click(object sender, RoutedEventArgs e) => ApplyCharacter("Noel", false);
    private void MaxNoel_Click(object sender, RoutedEventArgs e) => ApplyCharacter("Noel", true);
    private void ApplyLightning_Click(object sender, RoutedEventArgs e) => ApplyCharacter("Lightning DLC", false);
    private void MaxLightning_Click(object sender, RoutedEventArgs e) => ApplyCharacter("Lightning DLC", true);

    private void RefreshInventory()
    {
        string search = InventorySearchBox.Text.Trim();
        List<ItemRecord> owned = SaveCore.IterInventory(Plain, _data.ItemNames, _data.MonsterNames);
        // A malformed or unusual save can contain duplicate stack records. Do not silently
        // display one duplicate and edit another: duplicate IDs are surfaced read-only until
        // the save is normalized externally or the duplicate record layout is deliberately mapped.
        var grouped = owned.Where(r => r.Editable)
            .GroupBy(r => $"{r.BagIndex}/{r.ResolvedId}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<InventoryViewRow>();

        IEnumerable<ItemCatalog> visibleCatalog = _data.ItemCatalog.Where(i => i.Editable);
        if (!IsAdvancedInventoryMode())
            visibleCatalog = visibleCatalog.Where(i => !SaveCore.IsAdvancedInventoryBag(i.BagIndex));

        foreach (ItemCatalog item in visibleCatalog)
        {
            if (!Matches(search, item.Name, item.Category, item.Id)) continue;
            string key = $"{item.BagIndex}/{item.Id}";
            grouped.TryGetValue(key, out List<ItemRecord>? records);
            int quantity = records is { Count: > 0 } ? records[0].Qty : 0;
            bool duplicate = records is { Count: > 1 };
            rows.Add(new InventoryViewRow
            {
                Name = item.Name,
                Category = duplicate ? item.Category + " [duplicate records]" : item.Category,
                Quantity = quantity,
                Editable = !duplicate,
                Catalog = item
            });
        }
        foreach (ItemRecord record in owned.Where(r => !r.Editable))
        {
            if (!Matches(search, record.Name, record.Bag)) continue;
            rows.Add(new InventoryViewRow { Name = record.Name, Category = record.Bag, Quantity = record.Qty, Editable = false, Record = record });
        }
        _inventoryRows = rows.OrderBy(r => r.Category).ThenBy(r => r.Name).ToList();
        InventoryGrid.ItemsSource = _inventoryRows;
    }

    private void InventorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is not null && !_refreshing) RefreshInventory();
    }

    private void InventoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryViewRow row)
        {
            SelectedInventoryName.Text = "Select an item";
            InventoryQtyBox.Text = "0";
            InventoryQtyBox.IsEnabled = false;
            return;
        }
        SelectedInventoryName.Text = row.Name;
        InventoryQtyBox.Text = row.Quantity.ToString(CultureInfo.InvariantCulture);
        InventoryQtyBox.IsEnabled = row.Editable && _writeAllowed;
        if (row.Catalog is not null && row.Editable)
            StatusText.Text = $"{row.Name}: {row.Quantity} / {row.Catalog.MaxQty} • quantity editable";
    }

    private void ApplyInventory_Click(object sender, RoutedEventArgs e) => ApplyInventory(false);
    private void MaxInventory_Click(object sender, RoutedEventArgs e) => ApplyInventory(true);

    private void MaxAllSafeInventory_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (!Confirm("Max ordinary stackable inventory only?\n\nKey Items / OOPArts, Internal / Special, weapons, accessories and monster crystals are excluded.")) return;

        try
        {
            byte[] work = (byte[])Plain.Clone();
            int changed = 0;

            foreach (ItemCatalog item in _data.ItemCatalog.Where(i => i.Editable && SaveCore.IsSafeBulkQuantityBag(i.BagIndex)))
            {
                int before = SaveCore.CurrentCatalogQuantity(work, item);
                if (before == item.MaxQty) continue;
                SaveCore.SetCatalogQuantity(work, item, item.MaxQty);
                changed++;
            }

            if (changed == 0)
            {
                Status("Safe inventory is already maxed.");
                return;
            }

            _plain = SaveCore.RepairChecksum(work);
            MarkDirty($"Maxed {changed} safe inventory item(s)");
            RefreshInventory();
            RefreshGeneral();
            StatusText.Text = $"Safe inventory max complete • {changed} item(s) changed";
        }
        catch (Exception ex)
        {
            Error(ex.Message, "Max safe inventory failed");
        }
    }

    private void ApplyInventory(bool max)
    {
        if (!RequireEditable() || InventoryGrid.SelectedItem is not InventoryViewRow row || row.Catalog is null) return;
        if (!row.Editable) { Warn("This item has duplicate stack records in the save. Editing is disabled because changing only one duplicate would be ambiguous."); return; }
        if (SaveCore.IsAdvancedInventoryBag(row.Catalog.BagIndex) &&
            !Confirm("This is an Advanced Mode inventory entry. Key Item / OOPArts and Internal / Special edits can create impossible progression states. Continue?")) return;
        try
        {
            int value = max ? row.Catalog.MaxQty : checked((int)ParseBox(InventoryQtyBox, (uint)row.Catalog.MaxQty, "Quantity"));
            byte[] work = (byte[])Plain.Clone();
            SaveCore.SetCatalogQuantity(work, row.Catalog, value);
            _plain = SaveCore.RepairChecksum(work);
            MarkDirty(row.Name + " quantity updated");
            RefreshInventory();
            RefreshGeneral();
        }
        catch (Exception ex) { Error(ex.Message, "Inventory edit failed"); }
    }

    private void RefreshAdornments()
    {
        string search = AdornmentSearchBox.Text.Trim();

        _adornmentRows = _data.ItemCatalog
            .Where(SaveCore.IsAdornmentCatalogItem)
            .Where(item => Matches(search, item.Name, item.Id))
            .OrderBy(item => item.Name)
            .Select(item => new AdornmentViewRow
            {
                Id = item.Id,
                Name = item.Name,
                Owned = SaveCore.IsAdornmentOwned(Plain, item),
                Catalog = item
            })
            .ToList();

        AdornmentGrid.ItemsSource = null;
        AdornmentGrid.ItemsSource = _adornmentRows;

        int owned = _data.ItemCatalog
            .Where(SaveCore.IsAdornmentCatalogItem)
            .Count(item => SaveCore.IsAdornmentOwned(Plain, item));
        int total = _data.ItemCatalog.Count(SaveCore.IsAdornmentCatalogItem);
        AdornmentCountText.Text = $"{owned} / {total} owned";
    }

    private void AdornmentSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is not null && !_refreshing)
            RefreshAdornments();
    }

    private void SelectAllAdornments_Click(object sender, RoutedEventArgs e)
    {
        foreach (AdornmentViewRow row in _adornmentRows)
            row.Owned = true;
        AdornmentGrid.Items.Refresh();
    }

    private void ClearAllAdornments_Click(object sender, RoutedEventArgs e)
    {
        foreach (AdornmentViewRow row in _adornmentRows)
            row.Owned = false;
        AdornmentGrid.Items.Refresh();
    }

    private void ApplyAdornments_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;

        try
        {
            var staged = _adornmentRows.ToDictionary(r => r.Id, r => r.Owned, StringComparer.OrdinalIgnoreCase);
            List<ItemCatalog> all = _data.ItemCatalog
                .Where(SaveCore.IsAdornmentCatalogItem)
                .OrderBy(i => i.Name)
                .ToList();

            byte[] work = (byte[])Plain.Clone();
            foreach (ItemCatalog item in all)
            {
                bool desired = staged.TryGetValue(item.Id, out bool selected)
                    ? selected
                    : SaveCore.IsAdornmentOwned(Plain, item);
                SaveCore.SetAdornmentOwned(work, item, desired);
            }

            work = SaveCore.RepairChecksum(work);

            foreach (ItemCatalog item in all)
            {
                bool desired = staged.TryGetValue(item.Id, out bool selected)
                    ? selected
                    : SaveCore.IsAdornmentOwned(Plain, item);
                bool actual = SaveCore.IsAdornmentOwned(work, item);
                if (actual != desired)
                    throw new InvalidDataException($"Adornment verification failed for {item.Name}: expected {(desired ? "Owned" : "Not Owned")}.");
            }

            _plain = work;
            MarkDirty("Adornment ownership updated and verified");
            RefreshAdornments();
            RefreshInventory();
            RefreshGeneral();
        }
        catch (Exception ex)
        {
            Error(ex.Message, "Adornment edit failed");
        }
    }

    private void RefreshPartyGuests()
    {
        GuestPartyGrid.ItemsSource = null;
        GuestPartyGrid.ItemsSource = _guestPartyOptions;

        GuestPartyStatusText.Text =
            "Story Guest slot is research-only. No APP.DAT guest writer is enabled until the exact field and state dependencies are verified.";
    }

    private void GuestPartySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GuestPartyGrid.SelectedItem is not GuestPartyOption option)
            return;

        GuestPartySelectedText.Text = option.Name;
        GuestPartySelectedStatusText.Text = option.Status;
        GuestPartySelectedNoteText.Text = option.Note;
        ApplyGuestPartyButton.IsEnabled = option.CanWrite;
    }

    private void ApplyGuestParty_Click(object sender, RoutedEventArgs e)
    {
        Error("The real XIII-2 story guest slot is not mapped yet. This button stays disabled until the save field is verified.", "Guest Party");
    }

    private void RefreshEquipment()
    {
        List<EquipmentRecord> owned = SaveCore.OwnedEquipment(Plain, _data.ItemNames);
        _equipmentRows = owned.Select(r => new EquipmentViewRow { Type = r.Category, Name = r.Name, Slot = r.Slot + 1, State = "Owned", Record = r }).ToList();
        OwnedEquipmentGrid.ItemsSource = _equipmentRows;
        if (_equipmentRows.Count > 0 && OwnedEquipmentGrid.SelectedIndex < 0) OwnedEquipmentGrid.SelectedIndex = 0;
        RefreshEquipmentCatalog();
    }

    private void EquipmentSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is not null && !_refreshing) RefreshEquipmentCatalog();
    }

    private void RefreshEquipmentCatalog()
    {
        if (_plain is null || OwnedEquipmentGrid.SelectedItem is not EquipmentViewRow selected || selected.Record is null)
        {
            _equipmentCatalogRows = new();
            EquipmentCatalogGrid.ItemsSource = _equipmentCatalogRows;
            return;
        }
        EquipmentRecord current = selected.Record;
        string search = EquipmentSearchBox.Text.Trim();
        HashSet<string> ownedIds = SaveCore.OwnedEquipment(Plain, null)
            .Where(e => e.BagIndex == current.BagIndex)
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _equipmentCatalogRows = _data.ItemCatalog
            .Where(it => it.BagIndex == current.BagIndex)
            .Where(it => SaveCore.EquipmentReplacementAllowed(current.Id, it.Id, current.BagIndex))
            .Where(it => !it.Name.StartsWith("unused / unnamed", StringComparison.OrdinalIgnoreCase))
            .Where(it => Matches(search, it.Name, it.Id))
            .OrderBy(it => it.Name)
            .Select(it => new EquipmentCatalogViewRow
            {
                Name = it.Name,
                Type = current.BagIndex == 1 ? "Weapon" : "Accessory",
                Ownership = ownedIds.Contains(it.Id) ? "Owned" : "Not owned",
                Catalog = it
            }).ToList();
        EquipmentCatalogGrid.ItemsSource = _equipmentCatalogRows;
    }

    private void OwnedEquipmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshEquipmentCatalog();

    private void ReplaceEquipment_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (OwnedEquipmentGrid.SelectedItem is not EquipmentViewRow owned || owned.Record is null) { Warn("Select an owned equipment instance first."); return; }
        if (EquipmentCatalogGrid.SelectedItem is not EquipmentCatalogViewRow catalog || catalog.Catalog is null) { Warn("Select a replacement equipment item."); return; }
        if (owned.Record.Id == catalog.Catalog.Id) return;

        if (owned.Record.BagIndex == 2)
        {
            try
            {
                var equippedBy = new List<string>();
                foreach (string character in new[] { "Serah", "Noel" })
                    if (SaveCore.CharacterReferencesAccessory(Plain, character, owned.Record.Slot)) equippedBy.Add(character);
                if (equippedBy.Count > 0)
                {
                    Warn($"This accessory is currently equipped by {string.Join(" and ", equippedBy)}. Use Characters > Passives & Resistances to transform equipped accessories so capacity and equipment-reference safety are checked.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Error(ex.Message, "Equipped accessory verification failed");
                return;
            }
        }

        if (!Confirm($"Replace {owned.Name} with {catalog.Name}?\n\nThe existing equipment instance and save references are preserved.")) return;
        try
        {
            byte[] work = (byte[])Plain.Clone();
            SaveCore.WriteEquipmentCatalogId(work, owned.Record.BagIndex, owned.Record.Slot, catalog.Catalog.Id);
            _plain = SaveCore.RepairChecksum(work);
            MarkDirty($"{owned.Name} changed to {catalog.Name}");
            RefreshEquipment();
            RefreshInventory();
            if (owned.Record.BagIndex == 2)
            {
                RefreshCharacterAccessories("Serah");
                RefreshCharacterAccessories("Noel");
            }
        }
        catch (Exception ex) { Error(ex.Message, "Equipment edit failed"); }
    }

    private void AddMissingEquipment_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (EquipmentCatalogGrid.SelectedItem is not EquipmentCatalogViewRow row || row.Catalog is null) { Warn("Select a weapon from the catalog first."); return; }
        if (row.Catalog.BagIndex != 1) { Warn("Add Missing currently supports weapons only."); return; }
        if (SaveCore.WeaponOwned(Plain, row.Catalog.Id)) { Status(row.Name + " is already owned."); return; }
        if (!Confirm($"Add {row.Name} as a new unequipped weapon instance?")) return;
        try
        {
            var result = SaveCore.AddMissingWeapon(Plain, row.Catalog);
            _plain = result.Bytes;
            MarkDirty($"Added {row.Name} (weapon slot {result.Slot + 1})");
            RefreshEquipment();
            RefreshInventory();
        }
        catch (Exception ex) { Error(ex.Message, "Add weapon failed"); }
    }

    private void AddAllSerahWeapons_Click(object sender, RoutedEventArgs e) => AddAllWeapons("Serah");
    private void AddAllNoelWeapons_Click(object sender, RoutedEventArgs e) => AddAllWeapons("Noel");

    private void AddAllWeapons(string character)
    {
        if (!RequireEditable()) return;
        if (!Confirm($"Add every missing named {character} weapon as a new unequipped instance?")) return;
        try
        {
            var result = SaveCore.AddAllMissingWeapons(Plain, character, _data.ItemCatalog);
            _plain = result.Bytes;
            MarkDirty($"Added {result.Added} missing {character} weapons");
            RefreshEquipment();
            RefreshInventory();
        }
        catch (Exception ex) { Error(ex.Message, "Add weapons failed"); }
    }

    private void RefreshMonsters()
    {
        int previousSlot = _selectedMonster?.Slot ?? -1;
        bool wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            _allMonsterRows = SaveCore.ActiveMonsters(Plain, _data.MonsterNames, _data.MonsterLevelCaps);
            _abilityCountInfo = SaveCore.DetectMonsterAbilityCountField(Plain, _allMonsterRows);
            ApplyMonsterFilter(previousSlot);
            RefreshAvailableTraits();
            RefreshAvailableSkills();
            UpdateAbilityLayoutStatus();
            UpdateMonsterUndoButton();
            UpdateMonsterImageCoverage();
            UpdateUltimatePackButtons();
        }
        finally { _refreshing = wasRefreshing; }
    }

    private void ApplyMonsterFilter(int preferredSlot = -1)
    {
        string search = MonsterSearchBox.Text.Trim();
        bool missingRealOnly = MissingRealImagesOnlyCheckBox.IsChecked == true;
        _monsterRows = _allMonsterRows
            .Where(m => Matches(search, m.Name, m.Id, $"Lv {m.Level}", m.Level.ToString(CultureInfo.InvariantCulture),
                m.MaxLevel > 0 ? $"Max Lv {m.MaxLevel}" : "", m.LevelDisplay))
            .Where(m => !missingRealOnly || !ResolveMonsterImage(m.Id, m.Name).IsReal)
            .ToList();
        MonsterGrid.ItemsSource = _monsterRows;

        MonsterRecord? select = _monsterRows.FirstOrDefault(m => m.Slot == preferredSlot)
            ?? _monsterRows.FirstOrDefault(m => _selectedMonster is not null && m.Slot == _selectedMonster.Slot)
            ?? _monsterRows.FirstOrDefault();
        MonsterGrid.SelectedItem = select;
        if (select is not null) MonsterGrid.ScrollIntoView(select);
        FillSelectedMonster(select);
    }

    private void UpdateAbilityLayoutStatus()
    {
        if (_plain is null)
        {
            AbilityLayoutStatusText.Text = "Load a save to verify skill layout.";
            AbilityLayoutStatusText.ToolTip = null;
            return;
        }

        int mapped = _allMonsterRows.Count;
        if (_abilityCountInfo is not null)
        {
            AbilityLayoutStatusText.Text = $"{mapped} monsters • 32 skill + 10 passive slots • exact counters verified";
            AbilityLayoutStatusText.ToolTip = $"Verified from the loaded records: {_abilityCountInfo.Description}. Raw ability entry 0 is reserved/internal and is preserved; user skills occupy entries 1-32.";
        }
        else
        {
            AbilityLayoutStatusText.Text = $"{mapped} monsters • 32 skill + 10 passive slots • counter validation failed";
            AbilityLayoutStatusText.ToolTip = "This save does not match the verified BLES01269 active/passive counter layout. Count-changing edits are locked; replacements that preserve occupied-slot counts remain available.";
        }
    }

    private bool HasOwnedMonster(string monsterName) =>
        _allMonsterRows.Any(m => string.Equals(m.Name, monsterName, StringComparison.OrdinalIgnoreCase));

    private void UpdateUltimatePackButtons()
    {
        bool chichu = HasOwnedMonster("Chichu");
        bool cloudburst = HasOwnedMonster("Cloudburst");
        bool purple = HasOwnedMonster("Purple Chocobo");

        UltimateChichuButton.IsEnabled = chichu;
        UltimateCloudburstButton.IsEnabled = cloudburst;
        UltimatePurpleChocoboButton.IsEnabled = purple;

        UltimateChichuButton.Content = chichu ? "Chichu • COM ✓" : "Chichu • COM — not owned";
        UltimateCloudburstButton.Content = cloudburst ? "Cloudburst • RAV ✓" : "Cloudburst • RAV — not owned";
        UltimatePurpleChocoboButton.Content = purple ? "Purple • SYN ✓" : "Purple • SYN — not owned";
        UltimatePackStatusText.Text = $"Recommended core owned: {(chichu ? 1 : 0) + (cloudburst ? 1 : 0) + (purple ? 1 : 0)} / 3";
    }

    private bool SelectOwnedMonsterByName(string monsterName)
    {
        MonsterRecord? monster = _allMonsterRows.FirstOrDefault(m => string.Equals(m.Name, monsterName, StringComparison.OrdinalIgnoreCase));
        if (monster is null) return false;
        MonsterSearchBox.Text = "";
        if (MissingRealImagesOnlyCheckBox.IsChecked == true) MissingRealImagesOnlyCheckBox.IsChecked = false;
        ApplyMonsterFilter(monster.Slot);
        return true;
    }

    private void SelectUltimatePackMonster(string name, string role, string purpose)
    {
        if (_plain is null) { Error("Load a save first.", "Ultimate Paradigm Pack"); return; }
        if (!SelectOwnedMonsterByName(name)) { Error($"{name} is not currently present in the loaded tamed-monster records.", "Ultimate Paradigm Pack"); return; }
        StatusText.Text = $"Ultimate Pack: {name} ({role}) • {purpose}";
    }

    private void UltimatePackChichu_Click(object sender, RoutedEventArgs e) => SelectUltimatePackMonster("Chichu", "COM", "Main physical damage dealer");
    private void UltimatePackCloudburst_Click(object sender, RoutedEventArgs e) => SelectUltimatePackMonster("Cloudburst", "RAV", "Fast stagger builder; Friendly Fire support");
    private void UltimatePackPurpleChocobo_Click(object sender, RoutedEventArgs e) => SelectUltimatePackMonster("Purple Chocobo", "SYN", "General-purpose offensive support");

    private void MonsterSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is null || _refreshing) return;
        bool wasRefreshing = _refreshing;
        _refreshing = true;
        try { ApplyMonsterFilter(_selectedMonster?.Slot ?? -1); }
        finally { _refreshing = wasRefreshing; }
    }

    private void MonsterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        FillSelectedMonster(MonsterGrid.SelectedItem as MonsterRecord);
    }

    private void FillSelectedMonster(MonsterRecord? record)
    {
        _selectedMonster = record;
        RefreshChocoboRacePanel(record);
        if (record is null)
        {
            MonsterNameText.Text = "No monster selected";
            MonsterSlotText.Text = "";
            MonsterRoleText.Text = "";
            MonsterImage.Source = null;
            MonsterImageSourceText.Text = "No image";
            MonsterLevelLabel.Text = "Level";
            MonsterLevelBox.ToolTip = null;
            MonsterLevelBox.Text = MonsterHpBox.Text = MonsterStrengthBox.Text = MonsterMagicBox.Text = MonsterAtbBox.Text = "0";
            MonsterSkillCountText.Text = "0 / 32";
            MonsterPassiveCountText.Text = "0 / 10";
            MonsterSkillProgress.Value = 0;
            MonsterPassiveProgress.Value = 0;
            CurrentSkillsGrid.ItemsSource = null;
            CurrentTraitsGrid.ItemsSource = null;
            InfusionDonorCombo.ItemsSource = null;
            InfusionTransferGrid.ItemsSource = null;
            InfusionPreviewText.Text = "Select a target monster, then choose a donor.";
            return;
        }

        MonsterNameText.Text = record.Name;
        MonsterSlotText.Text = record.MaxLevel > 0
            ? $"Slot {record.Slot + 1} • {record.Id} • Max Lv {record.MaxLevel}"
            : $"Slot {record.Slot + 1} • {record.Id} • Max Lv unknown";
        string selectedRole = InferMonsterRole(record.Slot);
        MonsterRoleText.Text = string.IsNullOrEmpty(selectedRole) ? "Role: Unknown" : "Role: " + selectedRole;
        MonsterLevelLabel.Text = record.MaxLevel > 0 ? $"Level (max {record.MaxLevel})" : "Level (max unknown)";
        MonsterLevelBox.ToolTip = record.MaxLevel > 0
            ? (record.LevelOverCap
                ? $"Saved level {record.Level} is above this species' verified real cap of {record.MaxLevel}. Use Correct All Levels or save a valid value."
                : $"Verified real species cap: {record.MaxLevel}.")
            : "No verified species cap is mapped for this monster ID. Bulk max/correction preserves its current level.";
        MonsterLevelBox.Text = record.Level.ToString(CultureInfo.InvariantCulture);
        MonsterHpBox.Text = record.HP.ToString(CultureInfo.InvariantCulture);
        MonsterStrengthBox.Text = record.Strength.ToString(CultureInfo.InvariantCulture);
        MonsterMagicBox.Text = record.Magic.ToString(CultureInfo.InvariantCulture);
        MonsterAtbBox.Text = record.ATB.ToString(CultureInfo.InvariantCulture);
        LoadMonsterImage(record.Id, record.Name);

        string[] activeIds = SaveCore.MonsterAbilityIds(Plain, record.Slot);
        int activeCount = SaveCore.NonEmptyMonsterAbilityCount(Plain, record.Slot);
        MonsterSkillCountText.Text = $"{activeCount} / {SaveCore.MonsterEditableAbilityCount}";
        MonsterSkillProgress.Maximum = SaveCore.MonsterEditableAbilityCount;
        MonsterSkillProgress.Value = activeCount;
        _currentSkillRows = activeIds
            .Skip(SaveCore.MonsterFirstSkillSlot)
            .Select((id, index) => SkillRow(id, index + 1, index + SaveCore.MonsterFirstSkillSlot))
            .ToList();
        CurrentSkillsGrid.ItemsSource = _currentSkillRows;

        string[] passiveIds = SaveCore.MonsterPassiveIds(Plain, record.Slot);
        int passiveCount = passiveIds.Count(id => !string.IsNullOrEmpty(id));
        MonsterPassiveCountText.Text = $"{passiveCount} / {SaveCore.MonsterPassiveCount}";
        MonsterPassiveProgress.Value = passiveCount;
        CurrentTraitsGrid.ItemsSource = passiveIds.Select((id, index) => new TraitViewRow
        {
            Slot = index + 1,
            Name = string.IsNullOrEmpty(id) ? "Empty" : _data.PassiveNames.GetValueOrDefault(id, "Unmapped passive: " + id),
            Trait = _data.TraitsById.GetValueOrDefault(id)
        }).ToList();

        RefreshInfusionDonors();
    }

    private void RefreshChocoboRacePanel(MonsterRecord? record)
    {
        bool isChocobo = record is not null && SaveCore.IsChocoboMonster(record.Id, record.Name);
        bool canEditRace = isChocobo && _writeAllowed && !_loading;
        bool canEditRp = _plain is not null && _writeAllowed && !_loading;
        MaxChocoboRaceButton.IsEnabled = canEditRace;
        GodChocoboButton.IsEnabled = canEditRace;
        ApplyRaceRpButton.IsEnabled = canEditRp;

        if (_plain is not null)
        {
            uint currentRp = SaveCore.ReadValue(Plain, SaveCore.ChocoboRaceRpOffset, ValueKind.BE16);
            RaceRpText.Text = $"{currentRp} / {SaveCore.ChocoboRaceRpCap}";
            RaceRpBox.Text = currentRp.ToString(CultureInfo.InvariantCulture);
            RaceRpProgress.Value = Math.Min(currentRp, SaveCore.ChocoboRaceRpCap);
            RaceRpSourceText.Text = "Current Chocobo Racing profile • verified direct save field";
        }
        else
        {
            RaceRpText.Text = "-";
            RaceRpBox.Text = "0";
            RaceRpProgress.Value = 0;
            RaceRpSourceText.Text = "Load a save to read the current racing profile RP.";
        }

        if (record is null)
        {
            RaceEligibilityText.Text = "Select a chocobo in the Monster Library. RP can still be edited because it belongs to the current racing profile.";
            RaceSpeedText.Text = RaceStaminaText.Text = "-";
            RaceSpeedSourceText.Text = "Strength: -";
            RaceStaminaSourceText.Text = "Magic: -";
            RaceSpeedProgress.Value = RaceStaminaProgress.Value = 0;
            RefreshChocoboRaceAbilities(null, false);
            return;
        }

        if (!isChocobo)
        {
            RaceEligibilityText.Text = $"{record.Name} is not a chocobo. Select a tamed Chocobo, Gold, Silver, White, Black, Blue, Red, Green, or Purple Chocobo for Speed/Stamina and race abilities. RP is the separate current racing-profile value.";
            RaceSpeedText.Text = RaceStaminaText.Text = "N/A";
            RaceSpeedSourceText.Text = $"Strength: {record.Strength}";
            RaceStaminaSourceText.Text = $"Magic: {record.Magic}";
            RaceSpeedProgress.Value = RaceStaminaProgress.Value = 0;
            RefreshChocoboRaceAbilities(record, false);
            return;
        }

        uint speedValue = Math.Min(record.Strength, SaveCore.ChocoboRaceStatCap);
        uint staminaValue = Math.Min(record.Magic, SaveCore.ChocoboRaceStatCap);
        string speedGrade = SaveCore.ChocoboRaceGrade(record.Strength);
        string staminaGrade = SaveCore.ChocoboRaceGrade(record.Magic);

        RaceEligibilityText.Text = $"{record.Name} • Speed/Stamina derive from Strength/Magic; RP is stored separately in the current Chocobo Racing profile.";
        RaceSpeedText.Text = speedGrade + (speedValue >= SaveCore.ChocoboRaceStatCap ? " (MAX)" : "");
        RaceSpeedSourceText.Text = $"Strength: {record.Strength} • race value {speedValue} / {SaveCore.ChocoboRaceStatCap}";
        RaceSpeedProgress.Value = speedValue;

        RaceStaminaText.Text = staminaGrade + (staminaValue >= SaveCore.ChocoboRaceStatCap ? " (MAX)" : "");
        RaceStaminaSourceText.Text = $"Magic: {record.Magic} • race value {staminaValue} / {SaveCore.ChocoboRaceStatCap}";
        RaceStaminaProgress.Value = staminaValue;
        RefreshChocoboRaceAbilities(record, true);
    }

    private void ApplyChocoboRaceRp_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        try
        {
            uint rp = ParseBox(RaceRpBox, SaveCore.ChocoboRaceRpCap, "Chocobo Racing RP");
            uint current = SaveCore.ReadValue(Plain, SaveCore.ChocoboRaceRpOffset, ValueKind.BE16);
            if (current == rp)
            {
                Status($"Chocobo Racing RP is already {rp}.");
                return;
            }

            byte[] before = (byte[])Plain.Clone();
            _plain = SaveCore.ApplyFieldTransaction(Plain, new[]
            {
                new FieldWrite(SaveCore.ChocoboRaceRpOffset, ValueKind.BE16, rp, SaveCore.ChocoboRaceRpCap),
            });
            PushMonsterUndo(before);
            MarkDirty($"Chocobo Racing RP changed from {current} to {rp}");
            RefreshChocoboRacePanel(_selectedMonster);
        }
        catch (Exception ex) { Error(ex.Message, "Invalid Chocobo Racing RP"); }
    }

    private void RefreshChocoboRaceAbilities(MonsterRecord? record, bool isChocobo)
    {
        string previousName = (RaceAbilitiesGrid.SelectedItem as ChocoboRaceAbilityViewRow)?.Name ?? "";
        string[] passiveIds = record is not null && _plain is not null
            ? SaveCore.MonsterPassiveIds(Plain, record.Slot)
            : Array.Empty<string>();

        _raceAbilityRows = SaveCore.ChocoboRaceAbilities.Select(ability =>
        {
            string[] activeSources = passiveIds
                .Where(id => ability.PassiveIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Select(id => _data.PassiveNames.GetValueOrDefault(id, id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool active = isChocobo && activeSources.Length > 0;
            string preferredId = ability.PassiveIds.FirstOrDefault(id => _data.TraitsById.ContainsKey(id)) ?? "";
            string preferredName = string.IsNullOrEmpty(preferredId)
                ? "No mapped source"
                : _data.PassiveNames.GetValueOrDefault(preferredId, preferredId);

            return new ChocoboRaceAbilityViewRow
            {
                Status = !isChocobo ? "N/A" : active ? "Active" : "Available",
                Name = ability.Name,
                SourcePassive = active ? string.Join(", ", activeSources) : preferredName,
                Effect = ability.Effect,
                IsActive = active,
                Ability = ability
            };
        }).ToList();

        RaceAbilitiesGrid.ItemsSource = _raceAbilityRows;
        ChocoboRaceAbilityViewRow? select = _raceAbilityRows.FirstOrDefault(r => string.Equals(r.Name, previousName, StringComparison.OrdinalIgnoreCase))
            ?? _raceAbilityRows.FirstOrDefault();
        RaceAbilitiesGrid.SelectedItem = select;

        int activeCount = _raceAbilityRows.Count(r => r.IsActive);
        int passiveCount = passiveIds.Count(id => !string.IsNullOrEmpty(id));
        int godActiveCount = SaveCore.GodChocoboRaceAbilityNames.Count(name =>
            _raceAbilityRows.Any(row => row.IsActive && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)));
        RaceAbilityCountText.Text = isChocobo
            ? $"{activeCount} / {SaveCore.ChocoboRaceAbilities.Length} race abilities active • {passiveCount} / {SaveCore.MonsterPassiveCount} passive slots used • God preset {godActiveCount} / {SaveCore.GodChocoboRaceAbilityNames.Length}"
            : $"15 race abilities available • select a tamed chocobo to edit";
        UpdateRaceAbilityButtons();
    }

    private void RaceAbilitiesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateRaceAbilityButtons();

    private void UpdateRaceAbilityButtons()
    {
        bool eligible = _plain is not null && _writeAllowed && !_loading
            && _selectedMonster is not null
            && SaveCore.IsChocoboMonster(_selectedMonster.Id, _selectedMonster.Name)
            && RaceAbilitiesGrid.SelectedItem is ChocoboRaceAbilityViewRow;
        if (RaceAbilitiesGrid.SelectedItem is ChocoboRaceAbilityViewRow row)
        {
            AddRaceAbilityButton.IsEnabled = eligible && !row.IsActive;
            RemoveRaceAbilityButton.IsEnabled = eligible && row.IsActive;
        }
        else
        {
            AddRaceAbilityButton.IsEnabled = false;
            RemoveRaceAbilityButton.IsEnabled = false;
        }
    }

    private void AddChocoboRaceAbility_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (!SaveCore.IsChocoboMonster(_selectedMonster.Id, _selectedMonster.Name)) { Warn("Select a tamed chocobo first."); return; }
        if (RaceAbilitiesGrid.SelectedItem is not ChocoboRaceAbilityViewRow row || row.Ability is null) { Warn("Select a race ability first."); return; }

        ChocoboRaceAbility ability = row.Ability;
        string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
        if (SaveCore.ChocoboRaceAbilityActive(current, ability))
        {
            Status($"{ability.Name} is already active for {_selectedMonster.Name}.");
            return;
        }

        Trait? source = ability.PassiveIds
            .Select(id => _data.TraitsById.GetValueOrDefault(id))
            .FirstOrDefault(t => t is not null);
        if (source is null)
        {
            Warn($"No supported passive source for {ability.Name} is present in monster_traits.tsv.");
            return;
        }

        if (source.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase)
            && !Confirm($"{ability.Name} will be enabled by writing advanced passive '{source.Name}'. The awp_* prefix does not prove Red/Yellow Lock state, and that lock flag is not mapped.\n\nWrite this passive directly anyway?")) return;

        try
        {
            int target = SaveCore.ResolveTraitWriteTarget(Plain, _selectedMonster.Slot, source, _data.TraitsById);
            string oldId = current[target];
            bool addingToEmpty = string.IsNullOrEmpty(oldId);
            if (addingToEmpty && !EnsurePassiveCountChangeAllowed($"add {ability.Name} into an empty passive slot")) return;

            if (!string.IsNullOrEmpty(oldId) && oldId.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
            {
                string oldName = _data.PassiveNames.GetValueOrDefault(oldId, oldId);
                if (!Confirm($"Passive slot {target + 1} contains advanced passive '{oldName}'. Its lock state is unknown.\n\nOverwrite that slot with {source.Name}?")) return;
            }

            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, target, source.Id);
            if (addingToEmpty) SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"Chocobo race ability {ability.Name} added via {source.Name}");
            RefreshMonsters();
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message + "\n\nA chocobo has only 10 passive slots, so no more than 10 passive-derived race abilities can be active at once. Remove another passive/race ability first.");
        }
        catch (Exception ex) { Error(ex.Message, "Chocobo race ability edit failed"); }
    }

    private void RemoveChocoboRaceAbility_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (!SaveCore.IsChocoboMonster(_selectedMonster.Id, _selectedMonster.Name)) { Warn("Select a tamed chocobo first."); return; }
        if (RaceAbilitiesGrid.SelectedItem is not ChocoboRaceAbilityViewRow row || row.Ability is null) { Warn("Select a race ability first."); return; }

        ChocoboRaceAbility ability = row.Ability;
        string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
        var matchingIds = current
            .Where(id => !string.IsNullOrEmpty(id) && ability.PassiveIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matchingIds.Length == 0) { Status($"{ability.Name} is not active."); return; }
        if (!EnsurePassiveCountChangeAllowed($"remove the passive source for {ability.Name}")) return;

        string passiveList = string.Join(", ", matchingIds.Select(id => _data.PassiveNames.GetValueOrDefault(id, id)));
        if (!Confirm($"Remove {ability.Name} from {_selectedMonster.Name}?\n\nThis removes the granting passive(s): {passiveList}. Their normal battle effects will also be removed.")) return;
        if (matchingIds.Any(id => id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
            && !Confirm("At least one granting passive uses an advanced awp_* ID. Its actual Red/Yellow Lock state is not mapped. Remove it directly anyway?")) return;

        try
        {
            var removeSet = matchingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> keep = current.Where(id => string.IsNullOrEmpty(id) || !removeSet.Contains(id))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
            while (keep.Count < SaveCore.MonsterPassiveCount) keep.Add("");

            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            for (int i = 0; i < SaveCore.MonsterPassiveCount; i++)
                SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, i, keep[i]);
            SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"Chocobo race ability {ability.Name} removed");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Chocobo race ability edit failed"); }
    }

    private void ApplyGodChocoboPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (!SaveCore.IsChocoboMonster(_selectedMonster.Id, _selectedMonster.Name))
        {
            Warn("Select a tamed chocobo first.");
            return;
        }

        try
        {
            string selectedName = _selectedMonster.Name;
            ChocoboRaceAbility[] desiredAbilities = SaveCore.GodChocoboRaceAbilityNames
                .Select(name => SaveCore.ChocoboRaceAbilities.SingleOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Where(ability => ability is not null)
                .Select(ability => ability!)
                .ToArray();
            if (desiredAbilities.Length != SaveCore.MonsterPassiveCount)
            {
                Warn($"God Chocobo preset is incomplete: expected {SaveCore.MonsterPassiveCount} race abilities, resolved {desiredAbilities.Length}.");
                return;
            }

            Trait[] desiredTraits = desiredAbilities
                .Select(ability => ability.PassiveIds
                    .Select(id => _data.TraitsById.GetValueOrDefault(id))
                    .FirstOrDefault(trait => trait is not null))
                .Where(trait => trait is not null)
                .Select(trait => trait!)
                .ToArray();
            if (desiredTraits.Length != SaveCore.MonsterPassiveCount
                || desiredTraits.Select(trait => trait.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != SaveCore.MonsterPassiveCount)
            {
                Warn("God Chocobo preset could not resolve 10 unique passive sources against monster_traits.tsv. No changes were made.");
                return;
            }

            string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
            int currentCount = current.Count(id => !string.IsNullOrEmpty(id));
            if (currentCount != SaveCore.MonsterPassiveCount
                && !EnsurePassiveCountChangeAllowed("replace the current passive set with the 10-slot God Chocobo preset"))
                return;

            string abilityList = string.Join("\n", desiredAbilities.Select((ability, i) => $"  {i + 1}. {ability.Name}  ←  {desiredTraits[i].Name}"));
            string advancedList = string.Join(", ", desiredTraits
                .Where(trait => trait.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
                .Select(trait => trait.Name));
            string advancedNote = string.IsNullOrEmpty(advancedList)
                ? ""
                : $"\n\nAdvanced passive warning: {advancedList} use awp_* IDs. The editor does not map the actual per-monster Red/Yellow Lock flag; this preset writes those IDs directly.";

            if (!Confirm($"Apply the God Chocobo preset to {selectedName}?\n\n{abilityList}\n\nThis REPLACES all 10 current passive slots, so their normal battle effects are lost. It raises STR/MAG to at least 800 / 800 while preserving higher values and sets the direct Chocobo Racing RP value to 600.{advancedNote}\n\nThe entire change is one Monster Lab undo step."))
                return;

            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();

            for (int i = 0; i < SaveCore.MonsterPassiveCount; i++)
                SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, i, desiredTraits[i].Id);

            if (currentCount != SaveCore.MonsterPassiveCount)
                SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);

            int baseOffset = SaveCore.MonsterBase + _selectedMonster.Slot * SaveCore.MonsterStride;
            MonsterFieldSpec strengthSpec = SaveCore.MonsterFields["Strength"];
            MonsterFieldSpec magicSpec = SaveCore.MonsterFields["Magic"];
            uint strength = Math.Max(_selectedMonster.Strength, SaveCore.ChocoboRaceStatCap);
            uint magic = Math.Max(_selectedMonster.Magic, SaveCore.ChocoboRaceStatCap);

            SaveCore.WriteValue(work, baseOffset + strengthSpec.RelativeOffset, strengthSpec.Kind, strength);
            SaveCore.WriteValue(work, baseOffset + magicSpec.RelativeOffset, magicSpec.Kind, magic);
            SaveCore.WriteValue(work, SaveCore.ChocoboRaceRpOffset, ValueKind.BE16, SaveCore.ChocoboRaceRpCap);
            work = SaveCore.RepairChecksum(work);
            if (work.SequenceEqual(before))
            {
                Status($"{selectedName} already matches the God Chocobo preset, racing stat caps, and 600 RP.");
                return;
            }

            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("God Chocobo preset applied: best 10 race abilities + max racing stats + 600 RP");
            RefreshMonsters();
            RefreshGeneral();
            Status($"God Chocobo applied to {selectedName}: 10/10 preset abilities, direct RP 600, A-class race-stat caps.");
        }
        catch (Exception ex)
        {
            Error(ex.Message, "God Chocobo preset failed");
        }
    }

    private void MaxChocoboRaceStats_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (!SaveCore.IsChocoboMonster(_selectedMonster.Id, _selectedMonster.Name))
        {
            Warn("Select a tamed chocobo first.");
            return;
        }

        try
        {
            int baseOffset = SaveCore.MonsterBase + _selectedMonster.Slot * SaveCore.MonsterStride;
            MonsterFieldSpec strengthSpec = SaveCore.MonsterFields["Strength"];
            MonsterFieldSpec magicSpec = SaveCore.MonsterFields["Magic"];

            uint strength = Math.Max(_selectedMonster.Strength, SaveCore.ChocoboRaceStatCap);
            uint magic = Math.Max(_selectedMonster.Magic, SaveCore.ChocoboRaceStatCap);

            var writes = new[]
            {
                new FieldWrite(baseOffset + strengthSpec.RelativeOffset, strengthSpec.Kind, strength, strengthSpec.Max),
                new FieldWrite(baseOffset + magicSpec.RelativeOffset, magicSpec.Kind, magic, magicSpec.Max),
                new FieldWrite(SaveCore.ChocoboRaceRpOffset, ValueKind.BE16, SaveCore.ChocoboRaceRpCap, SaveCore.ChocoboRaceRpCap),
            };

            byte[] before = (byte[])Plain.Clone();
            byte[] work = SaveCore.ApplyFieldTransaction(Plain, writes);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("Chocobo racing stats maxed: STR/MAG caps + RP 600");
            RefreshMonsters();
            RefreshGeneral();
        }
        catch (Exception ex) { Error(ex.Message, "Chocobo racing edit failed"); }
    }

    private MonsterSkillViewRow SkillRow(string id, int slot = 0, int storageIndex = -1)
    {
        if (string.IsNullOrEmpty(id))
            return new MonsterSkillViewRow { Slot = slot, StorageIndex = storageIndex, Id = "", Name = "Empty", Role = "", Category = "" };
        if (_data.MonsterSkillsById.TryGetValue(id, out MonsterSkill? skill))
            return new MonsterSkillViewRow { Slot = slot, StorageIndex = storageIndex, Id = id, Name = skill.Name, Role = skill.Role, Category = skill.Category, Skill = skill };
        return new MonsterSkillViewRow { Slot = slot, StorageIndex = storageIndex, Id = id, Name = "Unmapped ability: " + id, Role = "Unknown", Category = "Raw ID" };
    }

    private string InferMonsterRole(int slot)
    {
        string mappedRole = SaveCore.MonsterRoleName(Plain, slot);
        if (!string.IsNullOrEmpty(mappedRole)) return mappedRole;

        string[] ids = SaveCore.MonsterAbilityIds(Plain, slot);
        var roles = ids
            .Select(id => _data.MonsterSkillsById.GetValueOrDefault(id))
            .Where(skill => skill is not null && !string.Equals(skill.Role, "Special", StringComparison.OrdinalIgnoreCase))
            .Select(skill => skill!.Role)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .GroupBy(role => role, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToList();
        return roles.Count == 0 ? "" : roles[0].Key;
    }

    private void ClearMonsterImageCache()
    {
        _monsterImageResolveCache.Clear();
        string manifest = Path.Combine(AppContext.BaseDirectory, "Assets", "monster_real_manifest.tsv");
        _monsterImageManifestStampUtc = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : DateTime.MinValue;
    }

    private void EnsureMonsterImageCacheFresh()
    {
        string manifest = Path.Combine(AppContext.BaseDirectory, "Assets", "monster_real_manifest.tsv");
        DateTime stamp = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : DateTime.MinValue;
        if (stamp != _monsterImageManifestStampUtc)
        {
            _monsterImageResolveCache.Clear();
            _monsterImageManifestStampUtc = stamp;
        }
    }

    private (string? Path, bool IsReal) ResolveMonsterImage(string id, string displayName)
    {
        EnsureMonsterImageCacheFresh();
        string cacheKey = $"{id}\n{displayName}";
        if (_monsterImageResolveCache.TryGetValue(cacheKey, out var cached))
            return cached;

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        string realDir = Path.Combine(assets, "monster_real");
        string? path = FindMonsterImageById(realDir, id);

        if (path is null && !string.IsNullOrWhiteSpace(displayName))
        {
            foreach (KeyValuePair<string, string> pair in _data.MonsterNames)
            {
                if (!string.Equals(pair.Value, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                path = FindMonsterImageById(realDir, pair.Key);
                if (path is not null) break;
            }
        }

        if (path is null)
            path = FindMonsterImageFromManifest(assets, id, displayName);

        if (path is not null)
        {
            var real = (path, true);
            _monsterImageResolveCache[cacheKey] = real;
            return real;
        }

        string builtInDir = Path.Combine(assets, "monsters");
        path = FindMonsterImageById(builtInDir, id);
        if (path is null && !string.IsNullOrWhiteSpace(displayName))
        {
            foreach (KeyValuePair<string, string> pair in _data.MonsterNames)
            {
                if (!string.Equals(pair.Value, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                path = FindMonsterImageById(builtInDir, pair.Key);
                if (path is not null) break;
            }
        }

        if (path is null)
        {
            string defaultPath = Path.Combine(builtInDir, "_default.png");
            if (File.Exists(defaultPath))
                path = defaultPath;
        }

        var fallback = (path, false);
        _monsterImageResolveCache[cacheKey] = fallback;
        return fallback;
    }

    private void UpdateMonsterImageCoverage()
    {
        if (_plain is null)
        {
            MonsterImageCoverageText.Text = "Images: load a save";
            return;
        }

        int real = 0;
        int fallback = 0;
        int missing = 0;
        foreach (MonsterRecord monster in _allMonsterRows)
        {
            var resolved = ResolveMonsterImage(monster.Id, monster.Name);
            if (resolved.Path is null) missing++;
            else if (resolved.IsReal) real++;
            else fallback++;
        }

        MonsterImageCoverageText.Text = $"Images: {real} real • {fallback} fallback • {missing} missing";
        MonsterImageCoverageText.ToolTip =
            "Real = your monster_real pack. Fallback = built-in icon. Missing = no matching local image.";
    }

    private void MissingRealImagesOnly_Checked(object sender, RoutedEventArgs e)
    {
        if (_plain is null || _refreshing) return;
        bool wasRefreshing = _refreshing;
        _refreshing = true;
        try { ApplyMonsterFilter(_selectedMonster?.Slot ?? -1); }
        finally { _refreshing = wasRefreshing; }
    }

    private void RefreshMonsterImages_Click(object sender, RoutedEventArgs e)
    {
        ClearMonsterImageCache();
        if (_plain is not null)
        {
            UpdateMonsterImageCoverage();
            ApplyMonsterFilter(_selectedMonster?.Slot ?? -1);
            if (_selectedMonster is not null)
                LoadMonsterImage(_selectedMonster.Id, _selectedMonster.Name);
        }
        Status("Monster image cache refreshed.");
    }

    private void OpenMonsterImagesFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "Assets", "monster_real");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex.Message, "Open image folder failed"); }
    }

    private void LoadMonsterImage(string id, string displayName)
    {
        try
        {
            var resolved = ResolveMonsterImage(id, displayName);
            if (resolved.Path is null)
            {
                MonsterImage.Source = null;
                MonsterImageSourceText.Text = "No image available";
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(resolved.Path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            MonsterImage.Source = image;
            MonsterImageSourceText.Text = resolved.IsReal
                ? "Real monster image • local pack"
                : "Built-in fallback icon";
        }
        catch
        {
            MonsterImage.Source = null;
            MonsterImageSourceText.Text = "Image could not be loaded";
        }
    }

    private static string? FindMonsterImageById(string directory, string id)
    {
        if (!Directory.Exists(directory) || string.IsNullOrWhiteSpace(id)) return null;
        foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" })
        {
            string candidate = Path.Combine(directory, id + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindMonsterImageFromManifest(string assetsDirectory, string id, string displayName)
    {
        string manifest = Path.Combine(assetsDirectory, "monster_real_manifest.tsv");
        if (!File.Exists(manifest)) return null;

        string? nameCandidate = null;
        foreach (string raw in File.ReadLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#", StringComparison.Ordinal)) continue;
            string[] parts = raw.Split('	');
            if (parts.Length < 3) continue;

            bool idMatch = !string.IsNullOrWhiteSpace(id)
                && string.Equals(parts[0].Trim(), id, StringComparison.OrdinalIgnoreCase);
            bool nameMatch = !string.IsNullOrWhiteSpace(displayName)
                && string.Equals(parts[1].Trim(), displayName, StringComparison.OrdinalIgnoreCase);
            if (!idMatch && !nameMatch) continue;

            string relative = parts[2].Trim().Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.Combine(assetsDirectory, relative);
            if (!File.Exists(candidate)) continue;
            if (idMatch) return candidate;
            nameCandidate ??= candidate;
        }
        return nameCandidate;
    }

    private void ApplyMonster_Click(object sender, RoutedEventArgs e) => ApplyMonster(false);
    private void MaxMonster_Click(object sender, RoutedEventArgs e) => ApplyMonster(true);

    private void ApplyMonster(bool max)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        try
        {
            int baseOffset = SaveCore.MonsterBase + _selectedMonster.Slot * SaveCore.MonsterStride;
            var boxMap = new Dictionary<string, WpfTextBox>
            {
                ["Level"] = MonsterLevelBox, ["HP"] = MonsterHpBox, ["Strength"] = MonsterStrengthBox,
                ["Magic"] = MonsterMagicBox, ["ATB"] = MonsterAtbBox
            };
            var writes = new List<FieldWrite>();
            foreach (var pair in boxMap)
            {
                MonsterFieldSpec spec = SaveCore.MonsterFields[pair.Key];
                uint fieldMax = spec.Max;
                bool isLevel = pair.Key.Equals("Level", StringComparison.OrdinalIgnoreCase);
                bool hasVerifiedLevelCap = isLevel && _selectedMonster.MaxLevel > 0;
                if (hasVerifiedLevelCap) fieldMax = _selectedMonster.MaxLevel;

                if (max && isLevel && !hasVerifiedLevelCap)
                    continue; // Never rewrite an unmapped/DLC level with a guessed validation cap.

                uint value = max ? fieldMax : ParseBox(pair.Value, fieldMax, pair.Key);
                writes.Add(new FieldWrite(baseOffset + spec.RelativeOffset, spec.Kind, value, fieldMax));
            }
            byte[] before = (byte[])Plain.Clone();
            byte[] work = SaveCore.ApplyFieldTransaction(Plain, writes);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("Tamed monster stats updated");
            RefreshMonsters();
            RefreshGeneral();
        }
        catch (Exception ex) { Error(ex.Message, "Monster edit failed"); }
    }

    private void MaxAllMonsters_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (!Confirm("Max the stats of every owned tamed monster? Each mapped species will use its verified real level cap. Unmapped/DLC monster levels are preserved rather than guessed.")) return;
        try
        {
            var writes = new List<FieldWrite>();
            foreach (MonsterRecord monster in SaveCore.ActiveMonsters(Plain, _data.MonsterNames, _data.MonsterLevelCaps))
            {
                int baseOffset = SaveCore.MonsterBase + monster.Slot * SaveCore.MonsterStride;
                foreach (var pair in SaveCore.MonsterFields)
                {
                    MonsterFieldSpec spec = pair.Value;
                    uint value = spec.Max;
                    uint fieldMax = spec.Max;
                    if (pair.Key.Equals("Level", StringComparison.OrdinalIgnoreCase))
                    {
                        if (monster.MaxLevel == 0) continue; // Preserve unknown/DLC level bytes exactly.
                        value = monster.MaxLevel;
                        fieldMax = monster.MaxLevel;
                    }
                    writes.Add(new FieldWrite(baseOffset + spec.RelativeOffset, spec.Kind, value, fieldMax));
                }
            }
            byte[] before = (byte[])Plain.Clone();
            byte[] work = SaveCore.ApplyFieldTransaction(Plain, writes);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("All owned tamed monsters maxed using real species level caps");
            RefreshMonsters();
            RefreshGeneral();
        }
        catch (Exception ex) { Error(ex.Message, "Max monsters failed"); }
    }


    private (int Monsters, int Fields, int Skipped) RestoreAllMonstersFromLoadedBaseline(bool markDirty = true)
    {
        if (_loadedBaselinePlain is null) return (0, 0, 0);

        List<MonsterRecord> original = SaveCore.ActiveMonsters(_loadedBaselinePlain, _data.MonsterNames, _data.MonsterLevelCaps);
        Dictionary<int, MonsterRecord> currentBySlot = SaveCore.ActiveMonsters(Plain, _data.MonsterNames, _data.MonsterLevelCaps)
            .ToDictionary(m => m.Slot);

        byte[] work = (byte[])Plain.Clone();
        int restoredMonsters = 0;
        int restoredFields = 0;
        int skipped = 0;

        foreach (MonsterRecord baselineMonster in original)
        {
            if (!currentBySlot.TryGetValue(baselineMonster.Slot, out MonsterRecord? currentMonster) ||
                !string.Equals(currentMonster.Id, baselineMonster.Id, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue; // Never write baseline stats onto a different monster occupying the slot.
            }

            int baseOffset = SaveCore.MonsterBase + baselineMonster.Slot * SaveCore.MonsterStride;
            int monsterChanges = 0;
            foreach (MonsterFieldSpec spec in SaveCore.MonsterFields.Values)
            {
                int offset = baseOffset + spec.RelativeOffset;
                uint originalValue = SaveCore.ReadValue(_loadedBaselinePlain, offset, spec.Kind);
                uint currentValue = SaveCore.ReadValue(work, offset, spec.Kind);
                if (currentValue == originalValue) continue;
                SaveCore.WriteValue(work, offset, spec.Kind, originalValue);
                restoredFields++;
                monsterChanges++;
            }

            if (monsterChanges > 0) restoredMonsters++;
        }

        if (restoredFields > 0)
        {
            byte[] before = (byte[])Plain.Clone();
            PushMonsterUndo(before);
            _plain = SaveCore.RepairChecksum(work);
            if (markDirty)
                MarkDirty($"Restored {restoredMonsters} monster(s) to loaded stat values");
        }

        return (restoredMonsters, restoredFields, skipped);
    }

    private void RestoreAllMonsters_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLoadedBaseline()) return;
        if (!Confirm("Restore Level, HP, Strength, Magic and ATB for every unchanged owned monster slot to the exact values from when this save was loaded?\n\nSkills, passives, infusion data, ownership and equipment are not changed.")) return;

        var result = RestoreAllMonstersFromLoadedBaseline();
        RefreshMonsters();
        RefreshGeneral();

        string skipped = result.Skipped > 0
            ? $" • {result.Skipped} slot(s) skipped because the monster ID no longer matches the loaded save"
            : "";
        Status(result.Fields == 0
            ? "All matching monster stats already match the loaded save." + skipped
            : $"Restored {result.Monsters} monster(s), {result.Fields} stat field(s){skipped}.");
    }

    private void RestoreSerahNoelAndMonsters_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLoadedBaseline()) return;
        if (!Confirm("Restore Serah, Noel and all matching owned monsters to their mapped stat values from when this save was loaded?\n\nThis does not restore equipment, abilities, inventory, story progression, monster skills/passives or ownership.")) return;

        int serah = RestoreCharacterFromLoadedBaseline("Serah", false);
        int noel = RestoreCharacterFromLoadedBaseline("Noel", false);
        var monsters = RestoreAllMonstersFromLoadedBaseline(false);

        if (serah + noel + monsters.Fields > 0)
            MarkDirty("Serah, Noel and monster mapped stats restored to loaded values");

        RefreshCharacters();
        RefreshMonsters();
        RefreshGeneral();

        string skipped = monsters.Skipped > 0 ? $" • {monsters.Skipped} monster slot(s) safely skipped" : "";
        Status($"Restore complete • Serah fields: {serah} • Noel fields: {noel} • Monster fields: {monsters.Fields}{skipped}");
    }

    private void CorrectAllMonsterLevels_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        try
        {
            List<MonsterRecord> monsters = SaveCore.ActiveMonsters(Plain, _data.MonsterNames, _data.MonsterLevelCaps);
            List<MonsterRecord> invalid = monsters
                .Where(m => m.MaxLevel > 0 && m.Level > m.MaxLevel)
                .ToList();

            if (invalid.Count == 0)
            {
                Info("Every mapped owned monster is already at or below its verified real species level cap. Unmapped/DLC IDs were left untouched.", "Monster levels are valid");
                return;
            }

            string preview = string.Join(Environment.NewLine, invalid.Take(8)
                .Select(m => $"• {m.Name}: {m.Level} → {m.MaxLevel}"));
            if (invalid.Count > 8) preview += $"{Environment.NewLine}• …and {invalid.Count - 8} more";
            if (!Confirm($"Correct {invalid.Count} monster level{(invalid.Count == 1 ? "" : "s")} that exceed the verified species cap? Only impossible over-cap levels are lowered; valid lower levels and unknown/DLC IDs are unchanged.\n\n{preview}")) return;

            MonsterFieldSpec levelSpec = SaveCore.MonsterFields["Level"];
            var writes = invalid.Select(monster => new FieldWrite(
                SaveCore.MonsterBase + monster.Slot * SaveCore.MonsterStride + levelSpec.RelativeOffset,
                levelSpec.Kind, monster.MaxLevel, monster.MaxLevel)).ToList();

            byte[] before = (byte[])Plain.Clone();
            byte[] work = SaveCore.ApplyFieldTransaction(Plain, writes);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"Corrected {invalid.Count} monster level{(invalid.Count == 1 ? "" : "s")} to real species caps");
            RefreshMonsters();
            RefreshGeneral();
        }
        catch (Exception ex) { Error(ex.Message, "Correct monster levels failed"); }
    }

    private void PushMonsterUndo(byte[] snapshot)
    {
        if (_monsterUndo.Count >= 8)
        {
            List<byte[]> keep = _monsterUndo.Reverse().Skip(1).ToList();
            _monsterUndo.Clear();
            foreach (byte[] item in keep) _monsterUndo.Push(item);
        }
        _monsterUndo.Push(snapshot);
        UpdateMonsterUndoButton();
    }

    private void UpdateMonsterUndoButton()
    {
        UndoMonsterButton.IsEnabled = _monsterUndo.Count > 0 && _plain is not null && !_loading;
        UndoMonsterButton.Content = _monsterUndo.Count > 0 ? $"Undo Monster Edit ({_monsterUndo.Count})" : "Undo Monster Edit";
    }

    private void UndoMonsterEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (_monsterUndo.Count == 0) { Status("No Monster Lab edits to undo."); return; }
        _plain = _monsterUndo.Pop();
        MarkDirty("Last Monster Lab edit undone");
        RefreshMonsters();
        RefreshGeneral();
    }

    private void SkillSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is not null) RefreshAvailableSkills();
    }

    private void RefreshAvailableSkills()
    {
        string search = SkillSearchBox.Text.Trim();
        _availableSkillRows = _data.MonsterSkills
            .Where(skill => Matches(search, skill.Name, skill.Role, skill.Category, skill.Id))
            .OrderBy(skill => RoleSort(skill.Role))
            .ThenBy(skill => skill.Name)
            .Select(skill => new MonsterSkillViewRow
            {
                Id = skill.Id,
                Name = skill.Name,
                Role = skill.Role,
                Category = skill.Category,
                Skill = skill
            })
            .ToList();
        AvailableSkillsGrid.ItemsSource = _availableSkillRows;
    }

    private static int RoleSort(string role) => role switch
    {
        "Commando" => 0,
        "Ravager" => 1,
        "Sentinel" => 2,
        "Saboteur" => 3,
        "Synergist" => 4,
        "Medic" => 5,
        "Special" => 6,
        _ => 7
    };

    private MonsterSkill? SelectedAvailableSkill()
        => AvailableSkillsGrid.SelectedItem is MonsterSkillViewRow row ? row.Skill : null;

    private static bool IsFeralLink(string id) => string.Equals(id, "rk000", StringComparison.OrdinalIgnoreCase);

    private bool ConfirmSkillRole(MonsterSkill skill)
    {
        if (_selectedMonster is null || string.Equals(skill.Role, "Special", StringComparison.OrdinalIgnoreCase)) return true;
        string targetRole = InferMonsterRole(_selectedMonster.Slot);
        if (string.IsNullOrEmpty(targetRole) || string.Equals(targetRole, skill.Role, StringComparison.OrdinalIgnoreCase)) return true;
        return Confirm($"{skill.Name} is catalogued as a {skill.Role} ability, while the selected monster appears to be {targetRole}.\n\nCross-role active skills are outside normal infusion rules and may not behave correctly in battle. Apply it anyway?");
    }

    private void ReplaceSkill_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (CurrentSkillsGrid.SelectedItem is not MonsterSkillViewRow current) { Warn("Select a current active-skill slot first."); return; }
        MonsterSkill? skill = SelectedAvailableSkill();
        if (skill is null) { Warn("Select an ability from the catalog first."); return; }
        if (IsFeralLink(current.Id)) { Warn("Feral Link is protected in safe mode and cannot be replaced."); return; }
        if (IsFeralLink(skill.Id)) { Warn("Feral Link cannot be inserted into another active-skill slot."); return; }
        if (!ConfirmSkillRole(skill)) return;
        try
        {
            string[] ids = SaveCore.MonsterAbilityIds(Plain, _selectedMonster.Slot);
            int duplicate = Array.FindIndex(ids, id => string.Equals(id, skill.Id, StringComparison.OrdinalIgnoreCase));
            if (duplicate >= 0 && duplicate != current.StorageIndex) { Warn($"{skill.Name} is already present in skill slot {duplicate}."); return; }
            if (current.Empty && _abilityCountInfo is null) { Warn("This slot is empty, but the save's ability-count field was not confidently detected. Use Replace on a non-empty slot, or load a save where the counter can be verified."); return; }

            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.WriteMonsterAbility(work, _selectedMonster.Slot, current.StorageIndex, skill.Id);
            if (current.Empty) SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"{skill.Name} written to active-skill slot {current.Slot}");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Skill edit failed"); }
    }

    private void AddSkill_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        MonsterSkill? skill = SelectedAvailableSkill();
        if (skill is null) { Warn("Select an ability from the catalog first."); return; }
        if (IsFeralLink(skill.Id)) { Warn("Feral Link cannot be added as a normal role ability."); return; }
        if (_abilityCountInfo is null) { Warn("Active-skill add/remove is locked because the ability-count field could not be proven from this save. Replacing existing slots is still available."); return; }
        if (!ConfirmSkillRole(skill)) return;
        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            int slot = SaveCore.AddMonsterAbility(work, _selectedMonster.Slot, skill.Id, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"{skill.Name} added to active-skill slot {slot + 1}");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Add skill failed"); }
    }

    private void RemoveSkill_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (CurrentSkillsGrid.SelectedItem is not MonsterSkillViewRow current || current.Empty) { Warn("Select a non-empty current active skill first."); return; }
        if (IsFeralLink(current.Id)) { Warn("Feral Link is protected and cannot be removed in safe mode."); return; }
        if (_abilityCountInfo is null) { Warn("Active-skill add/remove is locked because the ability-count field could not be proven from this save."); return; }
        if (!Confirm($"Remove {current.Name} from the selected monster?\n\nThe remaining active skills will be compacted to preserve a contiguous ability list.")) return;
        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.RemoveMonsterAbilityAt(work, _selectedMonster.Slot, current.StorageIndex, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty(current.Name + " removed from active skills");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Remove skill failed"); }
    }

    private void MoveSkillUp_Click(object sender, RoutedEventArgs e) => MoveSelectedSkill(-1);
    private void MoveSkillDown_Click(object sender, RoutedEventArgs e) => MoveSelectedSkill(1);

    private void MoveSelectedSkill(int direction)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (CurrentSkillsGrid.SelectedItem is not MonsterSkillViewRow current || current.Empty) { Warn("Select a non-empty current active skill first."); return; }
        if (IsFeralLink(current.Id)) { Warn("Feral Link stays in its existing slot in safe mode."); return; }
        int source = current.StorageIndex;
        int target = source + direction;
        if (target < SaveCore.MonsterFirstSkillSlot || target >= SaveCore.MonsterAbilityCount) return;
        string[] ids = SaveCore.MonsterAbilityIds(Plain, _selectedMonster.Slot);
        if (string.IsNullOrEmpty(ids[target])) { Warn("Skills are kept contiguous. Use Remove/Add rather than moving a skill across an empty slot."); return; }
        if (IsFeralLink(ids[target])) { Warn("Feral Link stays in its existing slot in safe mode."); return; }
        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.WriteMonsterAbility(work, _selectedMonster.Slot, source, ids[target]);
            SaveCore.WriteMonsterAbility(work, _selectedMonster.Slot, target, ids[source]);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"Moved {current.Name} {(direction < 0 ? "up" : "down")} in the active-skill order");
            RefreshMonsters();
            int displayIndex = target - SaveCore.MonsterFirstSkillSlot;
            if (displayIndex >= 0 && displayIndex < _currentSkillRows.Count)
            {
                CurrentSkillsGrid.SelectedIndex = displayIndex;
                CurrentSkillsGrid.ScrollIntoView(CurrentSkillsGrid.SelectedItem);
            }
        }
        catch (Exception ex) { Error(ex.Message, "Move skill failed"); }
    }

    private void CopySkills_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLoaded() || _selectedMonster is null) return;
        _copiedSkills = SaveCore.MonsterAbilityIds(Plain, _selectedMonster.Slot)
            .Skip(SaveCore.MonsterFirstSkillSlot)
            .ToArray();
        Status($"Copied all {SaveCore.MonsterEditableAbilityCount} editable skill slots from {_selectedMonster.Name}. Reserved raw entry 0 was not copied.");
    }

    private void PasteSkills_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (_copiedSkills is null || _copiedSkills.Length != SaveCore.MonsterEditableAbilityCount) { Warn("Copy active skills from another monster first."); return; }

        string[] rawCurrent = SaveCore.MonsterAbilityIds(Plain, _selectedMonster.Slot);
        string[] current = rawCurrent.Skip(SaveCore.MonsterFirstSkillSlot).ToArray();
        int currentCount = current.Count(id => !string.IsNullOrEmpty(id));

        string[] output;
        try
        {
            output = SaveCore.MergeCopiedEditableSkillsPreservingFeral(current, _copiedSkills);
        }
        catch (InvalidOperationException ex) { Warn(ex.Message); return; }

        int finalCount = output.Count(id => !string.IsNullOrEmpty(id));
        if (_abilityCountInfo is null && currentCount != finalCount)
        {
            Warn($"The final pasted skill set would change the occupied skill count from {currentCount} to {finalCount}. This save did not validate the known active/passive counters, so paste was blocked.");
            return;
        }

        if (!Confirm("Replace the selected monster's editable active-skill list with the copied list?\n\nFeral Link stays in its exact current slot and reserved raw ability entry 0 is preserved.")) return;
        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            for (int i = 0; i < SaveCore.MonsterEditableAbilityCount; i++)
                SaveCore.WriteMonsterAbility(work, _selectedMonster.Slot, i + SaveCore.MonsterFirstSkillSlot, output[i]);
            SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("Copied active-skill set pasted");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Paste skills failed"); }
    }

    private void PassiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not TraitViewRow row)
            return;

        PassiveDetailNameText.Text = row.Name;
        PassiveDetailCategoryText.Text = row.Category;
        PassiveDetailEffectText.Text = row.Description;
        PassiveAdvancedWarningText.Visibility = row.IsAdvanced ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TraitSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_plain is not null) RefreshAvailableTraits();
    }

    private void RefreshAvailableTraits()
    {
        string search = TraitSearchBox.Text.Trim();
        _availableTraitRows = _data.Traits
            .Where(t => Matches(search, t.Name, t.Category, t.Value, t.Id, SaveCore.DescribeTrait(t)))
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Name)
            .Select(t => new TraitViewRow { Name = t.Name, Trait = t })
            .ToList();
        AvailableTraitsGrid.ItemsSource = _availableTraitRows;
    }

    private bool PassiveCountChangesVerified => _abilityCountInfo is not null;

    private bool EnsurePassiveCountChangeAllowed(string action)
    {
        if (PassiveCountChangesVerified) return true;
        Warn($"Cannot {action} reliably on this save because the verified active/passive counter layout did not validate.\n\nYou can still upgrade/replace an occupied passive slot because that does not change the number of occupied passives.");
        return false;
    }

    private void ApplyPassivePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (sender is not Button button || button.Tag is not string requested) return;

        string presetKey = requested;
        string detectedRole = InferMonsterRole(_selectedMonster.Slot);
        if (string.Equals(requested, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            presetKey = detectedRole switch
            {
                "Commando" => "COM",
                "Ravager" => "RAV",
                "Medic" => "MED",
                "Sentinel" => "TANK",
                _ => "BALANCED"
            };
        }

        if (!PassivePresets.TryGetValue(presetKey, out string[]? presetIds)) return;
        List<Trait> desired = presetIds
            .Select(id => _data.TraitsById.GetValueOrDefault(id))
            .Where(trait => trait is not null)
            .Select(trait => trait!)
            .Where(trait => !trait.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (desired.Count == 0)
        {
            Warn("The selected preset could not be resolved against monster_traits.tsv.");
            return;
        }

        string presetName = presetKey switch
        {
            "COM" => "Best COM",
            "RAV" => "Best RAV",
            "MED" => "Best MED",
            "TANK" => "Tank",
            _ => "Balanced"
        };
        string autoNote = string.Equals(requested, "AUTO", StringComparison.OrdinalIgnoreCase)
            ? $"\nDetected role: {(string.IsNullOrEmpty(detectedRole) ? "Unknown" : detectedRole)} -> {presetName}."
            : "";
        string list = string.Join("\n", desired.Select(trait => "  • " + trait.Name));
        if (!Confirm($"Apply {presetName} passive recommendations to {_selectedMonster.Name}?{autoNote}\n\n{list}\n\nExisting advanced awp_* slots are preserved, and this preset will not add new awp_* passives because their per-monster Red/Yellow Lock metadata is not mapped. Existing normal non-preset passives may be replaced. You can use Undo Monster Edit afterward."))
            return;

        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            string[] current = SaveCore.MonsterPassiveIds(work, _selectedMonster.Slot);
            var protectedSlots = Enumerable.Range(0, current.Length)
                .Where(i => current[i].StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
                .ToHashSet();

            // awp_* is an advanced/role-passive namespace, not a proven lock flag. Presets preserve
            // existing awp_* slots conservatively because the per-monster Red/Yellow Lock state is unmapped.
            var protectedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int index in protectedSlots)
            {
                string id = current[index];
                protectedIds.Add(id);
                if (_data.TraitsById.TryGetValue(id, out Trait? lockedTrait) && !string.IsNullOrWhiteSpace(lockedTrait.Family))
                    protectedFamilies.Add(lockedTrait.Family);
            }
            desired = desired
                .Where(trait => !protectedIds.Contains(trait.Id)
                    && (string.IsNullOrWhiteSpace(trait.Family) || !protectedFamilies.Contains(trait.Family)))
                .ToList();

            var usedTargets = new HashSet<int>();
            int changed = 0;
            int added = 0;
            int satisfied = 0;
            int skipped = 0;

            foreach (Trait trait in desired)
            {
                current = SaveCore.MonsterPassiveIds(work, _selectedMonster.Slot);

                int exact = Array.FindIndex(current, id => string.Equals(id, trait.Id, StringComparison.OrdinalIgnoreCase));
                if (exact >= 0)
                {
                    satisfied++;
                    usedTargets.Add(exact);
                    continue;
                }

                // Upgrade/replace the same passive family first, preserving protected slots.
                int target = -1;
                if (!string.IsNullOrWhiteSpace(trait.Family))
                {
                    for (int i = 0; i < current.Length; i++)
                    {
                        if (protectedSlots.Contains(i) || usedTargets.Contains(i) || string.IsNullOrEmpty(current[i])) continue;
                        if (_data.TraitsById.TryGetValue(current[i], out Trait? existingTrait)
                            && string.Equals(existingTrait.Family, trait.Family, StringComparison.OrdinalIgnoreCase))
                        {
                            target = i;
                            break;
                        }
                    }
                }

                // When total ability count is verified, prefer an empty slot before replacing an
                // unrelated passive. Otherwise only occupied writable slots are eligible.
                if (target < 0 && PassiveCountChangesVerified)
                {
                    target = Enumerable.Range(0, current.Length)
                        .FirstOrDefault(i => !protectedSlots.Contains(i) && !usedTargets.Contains(i) && string.IsNullOrEmpty(current[i]), -1);
                }
                if (target < 0)
                {
                    target = Enumerable.Range(0, current.Length)
                        .FirstOrDefault(i => !protectedSlots.Contains(i) && !usedTargets.Contains(i) && !string.IsNullOrEmpty(current[i]), -1);
                }

                if (target < 0)
                {
                    skipped++;
                    continue;
                }

                bool wasEmpty = string.IsNullOrEmpty(current[target]);
                if (wasEmpty && !PassiveCountChangesVerified)
                {
                    skipped++;
                    continue;
                }

                SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, target, trait.Id);
                usedTargets.Add(target);
                changed++;
                if (wasEmpty) added++;
            }

            if (changed == 0)
            {
                Status($"{presetName}: no passive changes were needed. {satisfied} recommendation(s) already present; {skipped} could not be placed safely.");
                return;
            }

            if (added > 0)
                SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"{presetName} passive preset applied ({changed} changed, {satisfied} already present, {skipped} skipped)");
            RefreshMonsters();

            if (!PassiveCountChangesVerified && skipped > 0)
                Warn($"{presetName} was applied in count-neutral safe mode. {changed} occupied passive slot(s) were updated and {skipped} recommendation(s) were skipped because adding a new slot would change the unverified active/passive counter layout.");
        }
        catch (Exception ex)
        {
            Error(ex.Message, "Passive preset failed");
        }
    }

    private void AddTrait_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (AvailableTraitsGrid.SelectedItem is not TraitViewRow row || row.Trait is null) { Warn("Select an available passive first."); return; }
        if (row.Trait.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase)
            && !Confirm($"{row.Trait.Name} uses an advanced awp_* ID. That prefix does not prove Red/Yellow Lock state, and this editor has not mapped the per-monster lock flag.\n\nWrite this advanced passive directly anyway?")) return;

        try
        {
            string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
            int preferred = CurrentTraitsGrid.SelectedIndex;
            int target = SaveCore.ResolveTraitWriteTarget(Plain, _selectedMonster.Slot, row.Trait, _data.TraitsById, preferred, allowAdvancedOverwrite: false);
            string oldId = target >= 0 && target < current.Length ? current[target] : "";
            bool addingToEmpty = string.IsNullOrEmpty(oldId);

            // If this save does not validate the exact active/passive counters, adding a new
            // passive is unsafe. Still make Add / Upgrade useful: when the user explicitly
            // selected an occupied current slot, fall back to a count-neutral replacement.
            if (addingToEmpty && !PassiveCountChangesVerified
                && preferred >= 0 && preferred < current.Length
                && !string.IsNullOrEmpty(current[preferred]))
            {
                target = preferred;
                oldId = current[target];
                addingToEmpty = false;
            }
            if (addingToEmpty && !EnsurePassiveCountChangeAllowed("add a new passive into an empty slot")) return;

            if (string.Equals(oldId, row.Trait.Id, StringComparison.OrdinalIgnoreCase))
            {
                Status($"{row.Trait.Name} is already present in passive slot {target + 1}.");
                CurrentTraitsGrid.SelectedIndex = target;
                return;
            }

            bool overwritingAdvanced = !string.IsNullOrEmpty(oldId) && oldId.StartsWith("awp_", StringComparison.OrdinalIgnoreCase);
            if (overwritingAdvanced)
            {
                string oldName = _data.PassiveNames.GetValueOrDefault(oldId, oldId);
                if (!Confirm($"Passive slot {target + 1} contains advanced awp_* passive '{oldName}'. Its actual Red/Yellow Lock state is unknown.\n\nOverwrite that slot directly anyway?")) return;
            }

            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            // IMPORTANT: write to the target that was already resolved/validated above. Re-running
            // ResolveTraitWriteTarget here can choose an empty slot again, defeating the count-neutral
            // fallback selected for saves whose total ability-count field is not verified.
            SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, target, row.Trait.Id);
            bool addedToEmpty = addingToEmpty;
            if (addedToEmpty) SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            string action = addedToEmpty ? "added" : "replaced";
            MarkDirty($"{row.Trait.Name} {action} in passive slot {target + 1}");
            RefreshMonsters();
            CurrentTraitsGrid.SelectedIndex = target;
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
        catch (Exception ex) { Error(ex.Message, "Passive edit failed"); }
    }

    private void ReplaceTraitSlot_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        int selectedSlot = CurrentTraitsGrid.SelectedIndex;
        if (selectedSlot < 0 || selectedSlot >= SaveCore.MonsterPassiveCount) { Warn("Select one of the 10 current passive slots."); return; }
        if (AvailableTraitsGrid.SelectedItem is not TraitViewRow row || row.Trait is null) { Warn("Select an available passive first."); return; }

        string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
        string oldId = current[selectedSlot];
        if (string.IsNullOrEmpty(oldId) && !EnsurePassiveCountChangeAllowed("write a passive into an empty slot")) return;
        if (!string.IsNullOrEmpty(oldId) && oldId.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
        {
            string oldName = _data.PassiveNames.GetValueOrDefault(oldId, oldId);
            if (!Confirm($"Slot {selectedSlot + 1} contains advanced awp_* passive '{oldName}'. Its actual Red/Yellow Lock state is unknown.\n\nOverwrite that slot directly anyway?")) return;
        }
        if (row.Trait.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase)
            && !Confirm($"{row.Trait.Name} uses an advanced awp_* ID. Its actual per-monster lock state is unknown.\n\nWrite it directly into slot {selectedSlot + 1} anyway?")) return;

        try
        {
            if (string.Equals(oldId, row.Trait.Id, StringComparison.OrdinalIgnoreCase))
            {
                Status($"{row.Trait.Name} is already in passive slot {selectedSlot + 1}.");
                return;
            }
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, selectedSlot, row.Trait.Id);
            // Only adding into a previously empty slot can change a proven total-ability count.
            if (string.IsNullOrEmpty(oldId)) SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"Passive slot {selectedSlot + 1} replaced with {row.Trait.Name}");
            RefreshMonsters();
            CurrentTraitsGrid.SelectedIndex = selectedSlot;
        }
        catch (Exception ex) { Error(ex.Message, "Passive edit failed"); }
    }

    private void RemoveTrait_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        int index = CurrentTraitsGrid.SelectedIndex;
        if (index < 0 || index >= SaveCore.MonsterPassiveCount) { Warn("Select a current passive first."); return; }
        try
        {
            string[] ids = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
            if (string.IsNullOrEmpty(ids[index])) return;
            if (!EnsurePassiveCountChangeAllowed("remove a passive")) return;
            if (ids[index].StartsWith("awp_", StringComparison.OrdinalIgnoreCase) && !Confirm("This is an advanced awp_* passive. Its actual Red/Yellow Lock state is not mapped. Remove it directly anyway?")) return;
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.RemovePassiveAt(work, _selectedMonster.Slot, index);
            SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("Monster passive removed");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Passive edit failed"); }
    }

    private void ClearResists_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
        int removable = current.Count(id => !string.IsNullOrEmpty(id)
            && _data.TraitsById.TryGetValue(id, out Trait? trait)
            && SaveCore.IsResistanceCategory(trait.Category));
        if (removable == 0) { Status("No mapped resistance passives are present on this monster."); return; }
        if (!EnsurePassiveCountChangeAllowed("remove resistance passives")) return;
        if (!Confirm($"Remove {removable} mapped resistance passive(s) from this monster?")) return;
        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            SaveCore.ClearResistanceTraits(work, _selectedMonster.Slot, _data.TraitsById);
            SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("Monster resistance passives cleared");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Passive edit failed"); }
    }

    private void CopyTraits_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLoaded() || _selectedMonster is null) return;
        _copiedTraits = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
        Status("Copied all 10 passive slots from the selected monster.");
    }

    private void PasteTraits_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        if (_copiedTraits is null || _copiedTraits.Length != SaveCore.MonsterPassiveCount) { Warn("Copy passive slots from another monster first."); return; }
        string[] current = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot);
        int oldCount = current.Count(id => !string.IsNullOrEmpty(id));
        int newCount = _copiedTraits.Count(id => !string.IsNullOrEmpty(id));
        if (oldCount != newCount && !EnsurePassiveCountChangeAllowed("paste a passive set with a different occupied-slot count")) return;

        int[] advancedSlots = SaveCore.AdvancedPassivePasteChangeSlots(current, _copiedTraits);
        if (advancedSlots.Length > 0)
        {
            var advancedChanges = advancedSlots.Select(i =>
            {
                string oldId = current[i];
                string newId = _copiedTraits[i];
                string oldName = string.IsNullOrEmpty(oldId) ? "(empty)" : _data.PassiveNames.GetValueOrDefault(oldId, oldId);
                string newName = string.IsNullOrEmpty(newId) ? "(empty)" : _data.PassiveNames.GetValueOrDefault(newId, newId);
                return $"Slot {i + 1}: {oldName} -> {newName}";
            }).ToList();
            string detail = string.Join("\n", advancedChanges);
            if (!Confirm($"This paste changes {advancedSlots.Length} passive slot(s) involving advanced awp_* IDs. Their actual per-monster Red/Yellow Lock state is not mapped.\n\n{detail}\n\nWrite these advanced slot changes directly anyway?")) return;
        }

        bool anyChange = current.Where((id, i) => !string.Equals(id, _copiedTraits[i], StringComparison.OrdinalIgnoreCase)).Any();
        if (!anyChange) { Status("The copied passive set already matches this monster."); return; }
        if (!Confirm("Replace all 10 passive slots on the selected monster with the copied set?")) return;
        try
        {
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            for (int i = 0; i < _copiedTraits.Length; i++) SaveCore.WriteMonsterPassive(work, _selectedMonster.Slot, i, _copiedTraits[i]);
            SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty("Copied monster passives pasted");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Paste passives failed"); }
    }

    private void RefreshInfusionDonors()
    {
        if (_selectedMonster is null)
        {
            InfusionDonorCombo.ItemsSource = null;
            _infusionRows.Clear();
            InfusionTransferGrid.ItemsSource = null;
            return;
        }

        int previousDonorSlot = (InfusionDonorCombo.SelectedItem as MonsterRecord)?.Slot ?? -1;
        List<MonsterRecord> donors = _allMonsterRows.Where(m => m.Slot != _selectedMonster.Slot).ToList();
        InfusionDonorCombo.ItemsSource = donors;
        MonsterRecord? donor = donors.FirstOrDefault(m => m.Slot == previousDonorSlot) ?? donors.FirstOrDefault();
        InfusionDonorCombo.SelectedItem = donor;
        BuildInfusionRows(donor);
    }

    private void InfusionDonorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_plain is null || _selectedMonster is null) return;
        BuildInfusionRows(InfusionDonorCombo.SelectedItem as MonsterRecord);
    }

    private void BuildInfusionRows(MonsterRecord? donor)
    {
        _infusionRows = new List<InfusionTransferItem>();
        if (_selectedMonster is null || donor is null)
        {
            InfusionTransferGrid.ItemsSource = _infusionRows;
            InfusionPreviewText.Text = "Choose a donor monster.";
            return;
        }

        string targetRole = InferMonsterRole(_selectedMonster.Slot);
        string donorRole = InferMonsterRole(donor.Slot);
        HashSet<string> targetSkills = SaveCore.MonsterAbilityIds(Plain, _selectedMonster.Slot)
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> targetPassives = SaveCore.MonsterPassiveIds(Plain, _selectedMonster.Slot)
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string id in SaveCore.MonsterAbilityIds(Plain, donor.Slot))
        {
            if (string.IsNullOrEmpty(id) || IsFeralLink(id)) continue;
            if (!_data.MonsterSkillsById.TryGetValue(id, out MonsterSkill? skill))
            {
                _infusionRows.Add(new InfusionTransferItem { Include = false, Type = "Skill", Name = "Unmapped: " + id, Id = id, Role = "?", Note = "Unknown ID - not auto-transferred" });
                continue;
            }
            bool duplicate = targetSkills.Contains(id);
            bool sameRole = string.Equals(skill.Role, "Special", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(targetRole)
                || string.Equals(targetRole, skill.Role, StringComparison.OrdinalIgnoreCase);
            string note = duplicate ? "Already learned" : !sameRole ? $"Cross-role ({targetRole}) - use Skills tab" : "Transferable";
            _infusionRows.Add(new InfusionTransferItem
            {
                Include = !duplicate && sameRole,
                Type = "Skill",
                Name = skill.Name,
                Id = skill.Id,
                Role = skill.Role,
                Note = note,
                Skill = skill
            });
        }

        foreach (string id in SaveCore.MonsterPassiveIds(Plain, donor.Slot))
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (!_data.TraitsById.TryGetValue(id, out Trait? trait))
            {
                _infusionRows.Add(new InfusionTransferItem { Include = false, Type = "Passive", Name = "Unmapped: " + id, Id = id, Note = "Unknown ID - preserved only" });
                continue;
            }
            bool duplicate = targetPassives.Contains(id);
            bool advancedLockUnknown = id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase);
            _infusionRows.Add(new InfusionTransferItem
            {
                Include = !duplicate && !advancedLockUnknown,
                Type = "Passive",
                Name = trait.Name,
                Id = trait.Id,
                Role = "Any",
                Note = duplicate ? "Already present" : advancedLockUnknown ? "Advanced awp_* passive - lock state unknown; opt in manually" : trait.Category,
                Trait = trait
            });
        }

        InfusionTransferGrid.ItemsSource = _infusionRows;
        InfusionPreviewText.Text = $"Target: {_selectedMonster.Name} ({(string.IsNullOrEmpty(targetRole) ? "role unknown" : targetRole)})\nDonor: {donor.Name} ({(string.IsNullOrEmpty(donorRole) ? "role unknown" : donorRole)})\n\nSelect exactly what to transfer, then Preview. The donor is deliberately preserved; destructive crystal consumption is not enabled until the monster-crystal removal/ownership structures are verified.";
    }

    private void InfusionSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (InfusionTransferItem item in _infusionRows)
        {
            if (item.Skill is not null && item.Note.StartsWith("Cross-role", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Skill is null && item.Trait is null) continue;
            if (item.Note.StartsWith("Already", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Note.StartsWith("Advanced awp_", StringComparison.OrdinalIgnoreCase)) continue;
            item.Include = true;
        }
        InfusionTransferGrid.Items.Refresh();
    }

    private void InfusionClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (InfusionTransferItem item in _infusionRows) item.Include = false;
        InfusionTransferGrid.Items.Refresh();
    }

    private string BuildInfusionPreview(out bool canApply)
    {
        canApply = false;
        if (_selectedMonster is null) return "Select a target monster first.";
        if (InfusionDonorCombo.SelectedItem is not MonsterRecord donor) return "Choose a donor monster.";
        List<InfusionTransferItem> selected = _infusionRows.Where(item => item.Include).ToList();
        if (selected.Count == 0) return "Select at least one mapped skill or passive to transfer.";

        string targetRole = InferMonsterRole(_selectedMonster.Slot);
        string donorRole = InferMonsterRole(donor.Slot);
        var lines = new List<string>
        {
            $"Target: {_selectedMonster.Name} ({(string.IsNullOrEmpty(targetRole) ? "Unknown role" : targetRole)})",
            $"Donor: {donor.Name} ({(string.IsNullOrEmpty(donorRole) ? "Unknown role" : donorRole)})",
            $"Selected: {selected.Count(item => item.Skill is not null)} active skill(s), {selected.Count(item => item.Trait is not null)} passive(s)",
            ""
        };

        try
        {
            byte[] simulation = (byte[])Plain.Clone();
            foreach (InfusionTransferItem item in selected.Where(item => item.Skill is not null))
            {
                MonsterSkill skill = item.Skill!;
                if (IsFeralLink(skill.Id)) throw new InvalidOperationException("Feral Link is protected and is never transferred.");
                if (!string.Equals(skill.Role, "Special", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(targetRole)
                    && !string.Equals(skill.Role, targetRole, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{skill.Name} is {skill.Role}, but the target is {targetRole}. Cross-role active abilities are blocked in Infusion; use the Skills tab for explicit advanced customization.");
                SaveCore.AddMonsterAbility(simulation, _selectedMonster.Slot, skill.Id, _abilityCountInfo);
                lines.Add("+ Skill: " + skill.Name);
            }
            foreach (InfusionTransferItem item in selected.Where(item => item.Trait is not null))
            {
                if (item.Trait!.Id.StartsWith("awp_", StringComparison.OrdinalIgnoreCase))
                    lines.Add("  ! Lock-state warning: " + item.Trait.Name + " uses an advanced awp_* ID; donor Red/Yellow Lock state is not mapped, so this is a direct save transfer rather than a guaranteed game-authentic infusion.");
                string[] passivesBefore = SaveCore.MonsterPassiveIds(simulation, _selectedMonster.Slot);
                int target = SaveCore.ResolveTraitWriteTarget(simulation, _selectedMonster.Slot, item.Trait!, _data.TraitsById);
                bool addsSlot = string.IsNullOrEmpty(passivesBefore[target]);
                if (addsSlot && !PassiveCountChangesVerified)
                    throw new InvalidOperationException($"{item.Trait.Name} needs a new passive slot, but this save's active/passive counters are not verified. Upgrade/replace an occupied passive instead.");
                SaveCore.AddOrReplaceTrait(simulation, _selectedMonster.Slot, item.Trait!, _data.TraitsById);
                if (addsSlot) SaveCore.UpdateMonsterAbilityCount(simulation, _selectedMonster.Slot, _abilityCountInfo);
                lines.Add("+ Passive: " + item.Trait!.Name);
            }
            SaveCore.RepairChecksum(simulation);
            lines.Add("");
            lines.Add("Validation: PASS - target buffer, slot capacity and checksum are valid.");
            lines.Add("Safety: donor crystal is NOT consumed or deleted.");
            canApply = true;
        }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add("Validation: BLOCKED - " + ex.Message);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void PreviewInfusion_Click(object sender, RoutedEventArgs e)
    {
        InfusionPreviewText.Text = BuildInfusionPreview(out _);
    }

    private void ApplyInfusion_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _selectedMonster is null) return;
        string preview = BuildInfusionPreview(out bool canApply);
        InfusionPreviewText.Text = preview;
        if (!canApply) return;
        if (InfusionDonorCombo.SelectedItem is not MonsterRecord donor) return;
        if (!Confirm($"Apply the selected infusion from {donor.Name} to {_selectedMonster.Name}?\n\nThis is a safe manual transfer: the donor remains owned and is not consumed.")) return;

        try
        {
            List<InfusionTransferItem> selected = _infusionRows.Where(item => item.Include).ToList();
            byte[] before = (byte[])Plain.Clone();
            byte[] work = (byte[])Plain.Clone();
            foreach (InfusionTransferItem item in selected.Where(item => item.Skill is not null))
                SaveCore.AddMonsterAbility(work, _selectedMonster.Slot, item.Skill!.Id, _abilityCountInfo);
            foreach (InfusionTransferItem item in selected.Where(item => item.Trait is not null))
            {
                string[] passivesBefore = SaveCore.MonsterPassiveIds(work, _selectedMonster.Slot);
                int target = SaveCore.ResolveTraitWriteTarget(work, _selectedMonster.Slot, item.Trait!, _data.TraitsById);
                bool addsSlot = string.IsNullOrEmpty(passivesBefore[target]);
                if (addsSlot && !PassiveCountChangesVerified)
                    throw new InvalidOperationException($"{item.Trait!.Name} requires a new passive slot but the active/passive counters are unverified.");
                SaveCore.AddOrReplaceTrait(work, _selectedMonster.Slot, item.Trait!, _data.TraitsById);
                if (addsSlot) SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            }
            SaveCore.UpdateMonsterAbilityCount(work, _selectedMonster.Slot, _abilityCountInfo);
            work = SaveCore.RepairChecksum(work);
            PushMonsterUndo(before);
            _plain = work;
            MarkDirty($"Manual infusion applied from {donor.Name} to {_selectedMonster.Name}");
            RefreshMonsters();
        }
        catch (Exception ex) { Error(ex.Message, "Infusion failed"); }
    }


    private void RefreshFragments()
    {
        RefreshFragmentSkills();
    }

    private void RefreshGeneralProgressOnly()
    {
        if (_plain is null) return;
        int fragments = _session?.FragmentCount ?? -1;
        FragmentsProgressText.Text = fragments >= 0
            ? $"{fragments} / {SaveCore.FragmentCount}"
            : $"? / {SaveCore.FragmentCount}";
        MonstersProgressText.Text = SaveCore.ActiveMonsters(Plain, _data.MonsterNames, _data.MonsterLevelCaps).Count.ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshFragmentSkills()
    {
        uint mask = SaveCore.FragmentSkillMask(Plain);
        bool allUnlocked = SaveCore.AllFragmentSkillsUnlocked(mask);
        FragmentSkillsMaskText.Text = allUnlocked
            ? $"Mask: 0x{mask:X8}  |  All 14 known Fragment Skills unlocked"
            : $"Mask: 0x{mask:X8}  |  Verified all-unlocked known bits: 0x{SaveCore.FragmentSkillsAllMask:X8}";
        FragmentSkillsGrid.ItemsSource = SaveCore.FragmentSkills.Select(skill => new FragmentSkillViewRow
        {
            Name = skill.Name,
            Unlocked = SaveCore.FragmentSkillUnlocked(mask, skill) ? "Yes" : "No",
            Effect = skill.Description,
            Skill = skill
        }).ToList();
    }


    private void ToggleFragmentSkill_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable()) return;
        if (FragmentSkillsGrid.SelectedItem is not FragmentSkillViewRow row || row.Skill is null) { Warn("Select a fragment skill first."); return; }
        byte[] work = (byte[])Plain.Clone();
        bool current = SaveCore.FragmentSkillUnlocked(SaveCore.FragmentSkillMask(work), row.Skill);
        SaveCore.SetFragmentSkill(work, row.Skill, !current);
        _plain = SaveCore.RepairChecksum(work);
        MarkDirty("Fragment skill updated");
        RefreshFragmentSkills();
    }

    private void UnlockAllSkills_Click(object sender, RoutedEventArgs e) => SetAllSkills(true);
    private void ClearAllSkills_Click(object sender, RoutedEventArgs e) => SetAllSkills(false);

    private void SetAllSkills(bool on)
    {
        if (!RequireEditable()) return;

        uint before = SaveCore.FragmentSkillMask(Plain);
        uint expectedKnown = on ? SaveCore.FragmentSkillsAllMask : 0U;
        if ((before & SaveCore.FragmentSkillsAllMask) == expectedKnown)
        {
            Status(on ? "All 14 known Fragment Skills are already unlocked." : "All known Fragment Skills are already clear.");
            return;
        }

        if (!on && !Confirm("Clear all 14 known Fragment Skills? Unknown/reserved bits outside the verified mask will be preserved."))
            return;

        byte[] work = (byte[])Plain.Clone();
        SaveCore.SetAllFragmentSkills(work, on);
        _plain = SaveCore.RepairChecksum(work);
        MarkDirty(on ? $"All Fragment Skills unlocked (verified known mask 0x{SaveCore.FragmentSkillsAllMask:X8})" : "All known Fragment Skills cleared");
        RefreshFragmentSkills();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (_plain is null || _refreshing || _loading) return;
        // Lazy refresh: only parse/populate the tab the user actually opens.
        RefreshSelectedTab();
    }

    private void ExportDecrypted_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLoaded()) return;
        try
        {
            string dir = EditorOutputDir("Exports");
            string path = Path.Combine(dir, $"APP_DECRYPTED_{Stamp()}.DAT");
            File.WriteAllBytes(path, SaveCore.RepairChecksum(Plain));
            Info("Created decrypted backup:\n" + path, "Decrypted backup");
            Status("Decrypted APP.DAT backup exported.");
        }
        catch (Exception ex) { Error(ex.Message, "Export failed"); }
    }

    private void SaveEncryptedCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _session is null) return;
        try
        {
            byte[] encrypted = EncryptedBytes();
            SaveCore.VerifyEncryptedRoundtrip(encrypted, _session.WorkingKey, Plain);
            string dir = EditorOutputDir("Exports");
            string path = Path.Combine(dir, $"APP_EDITED_{Stamp()}.DAT");
            WriteFlushed(path, encrypted);
            SaveCore.VerifyEncryptedFile(path, _session.WorkingKey, Plain);
            Info("Created and verified:\n" + path, "Encrypted copy saved");
            Status("Encrypted edited copy saved and verified.");
        }
        catch (Exception ex) { Error(ex.Message, "Save failed"); }
    }

    private void SaveInPlace_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEditable() || _session is null) return;
        if (_session.PfdNames.Any(n => n.Equals("APP.DAT", StringComparison.OrdinalIgnoreCase)))
        {
            Warn("APP.DAT is listed as PARAM.PFD-protected. In-place saving is disabled because PARAM.PFD would need rebuilding.");
            return;
        }
        if (!Confirm("Save changes directly to this PS3 save folder?\n\nAn external backup of the original APP.DAT will be created first.")) return;

        string original = Path.Combine(_session.Folder, "APP.DAT");
        string? backup = null;
        byte[]? originalBytes = null;
        try
        {
            byte[] encrypted = EncryptedBytes();
            SaveCore.VerifyEncryptedRoundtrip(encrypted, _session.WorkingKey, Plain);
            originalBytes = File.ReadAllBytes(original);
            string backupDir = EditorOutputDir("Backups");
            backup = Path.Combine(backupDir, $"APP_{Stamp()}.DAT");
            WriteFlushed(backup, originalBytes);
            if (!File.ReadAllBytes(backup).SequenceEqual(originalBytes)) throw new IOException("Backup readback did not match original APP.DAT");

            string temp = Path.Combine(_session.Folder, $".APP.DAT.verify_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}");
            WriteFlushed(temp, encrypted);
            SaveCore.VerifyEncryptedFile(temp, _session.WorkingKey, Plain);
            File.Delete(temp);

            AtomicReplace(original, encrypted);
            SaveCore.VerifyEncryptedFile(original, _session.WorkingKey, Plain);
            _plain = SaveCore.RepairChecksum(Plain);
            _dirty = false;
            Info("APP.DAT updated and verified.\nBackup:\n" + backup, "Save complete");
            Status("APP.DAT saved safely and verified from disk.");
        }
        catch (Exception ex)
        {
            if (originalBytes is not null)
            {
                try
                {
                    AtomicReplace(original, originalBytes);
                    byte[] restored = File.ReadAllBytes(original);
                    if (!restored.SequenceEqual(originalBytes))
                        throw new IOException("Rollback readback did not match the original APP.DAT bytes");
                }
                catch (Exception rollback) { Error($"Save failed and rollback also failed.\n\nSave error: {ex.Message}\nRollback error: {rollback.Message}\nBackup: {backup}", "Recovery required"); return; }
            }
            Error($"Save failed. Original APP.DAT was restored when possible.\n\n{ex.Message}\nBackup: {backup}", "Save failed");
        }
    }

    private byte[] EncryptedBytes()
    {
        if (_session is null || _session.WorkingKey == 0) throw new InvalidOperationException("No trustworthy FFXIII-2 encryption key is loaded");
        return SaveCore.EncryptBytes(SaveCore.RepairChecksum(Plain), _session.WorkingKey);
    }

    private string EditorOutputDir(string kind)
    {
        string title = string.IsNullOrWhiteSpace(_session?.TitleId) ? "UnknownRegion" : _session!.TitleId;
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string dir = Path.Combine(docs, "FFXIII2 Save Editor WPF", kind, title);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);

    private static void WriteFlushed(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(data, 0, data.Length);
        stream.Flush(true);
    }

    private static void AtomicReplace(string target, byte[] data)
    {
        string dir = Path.GetDirectoryName(target)!;
        string temp = Path.Combine(dir, $".{Path.GetFileName(target)}.tmp_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}");
        try
        {
            WriteFlushed(temp, data);
            File.Move(temp, target, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private string? ChooseSaveFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Final Fantasy XIII-2 APP.DAT|APP.DAT|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            FileName = "APP.DAT"
        };

        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;

        if (dialog.ShowDialog(this) != true) return null;
        string? folder = Path.GetDirectoryName(dialog.FileName);
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    private static uint ParseBox(WpfTextBox box, uint max, string field)
    {
        if (!uint.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            throw new InvalidDataException(field + ": enter a whole number");
        if (value > max) throw new InvalidDataException($"{field}: maximum is {max}");
        return value;
    }

    private static bool Matches(string search, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return values.Any(v => v?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private bool ConfirmDiscardChanges(string action)
    {
        if (!_dirty) return true;
        return MessageBox.Show(this,
            $"There are unsaved changes in the currently loaded save.\n\nDiscard those changes and {action}?",
            "Unsaved changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_loading)
        {
            if (MessageBox.Show(this,
                "A save is still loading. Close the editor anyway?",
                "Loading in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        else if (_dirty && MessageBox.Show(this,
            "There are unsaved changes. Close the editor without saving them?",
            "Unsaved changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void Status(string message) => StatusText.Text = (_dirty ? "Modified - " : "") + message;
    private void Warn(string message) => MessageBox.Show(this, message, "FFXIII-2 Save Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void Error(string message, string title) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    private void Info(string message, string title) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    private bool Confirm(string message) => MessageBox.Show(this, message, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
