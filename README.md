# Final Fantasy XIII-2 PS3 Save Editor

Windows save editor for **Final Fantasy XIII-2 on PlayStation 3**.

## Latest release: v1.9.33

### Chocobo Racing

- Direct **RP editing from 0 to 600**.
- Verified decrypted `APP.DAT` RP field: **`0x10CDC` (BE16)**.
- **Max Racing Stats** and **God Chocobo** now use the real saved RP field instead of deriving RP from HP.
- Verified nearby Coins Won field: **`0x10CE4` (BE32)**.

### Other editor features

- Character and inventory editing.
- Tamed monster editing.
- Real monster level-cap data.
- Monster skills, passives and traits.
- Fragment Skills.
- Casino record editing.
- Save checksum repair / guarded write flow.

## Download

Use the GitHub release page:

**https://github.com/alsharfa/Final-Fantasy-XIII-2-PS3-Save-Editor/releases/tag/v1.9.33**

Release assets:

- `v1.9.33-win-x64-folder.zip` — self-contained Windows x64 build.
- `FFXIII2_PS3_Save_Editor_v1.9.33_SOURCE.zip` — matching source package.

Extract the Windows ZIP and run `FFXIII2SaveEditor.exe` or `RUN_EDITOR.bat`.

## Build from source

Requirements:

- Windows 10/11 x64
- .NET 8 SDK

Build the editor with:

```powershell
dotnet restore FFXIII2SaveEditor\FFXIII2SaveEditor.csproj
dotnet build FFXIII2SaveEditor\FFXIII2SaveEditor.csproj -c Release
```

Create a self-contained Windows x64 folder build with:

```powershell
dotnet publish FFXIII2SaveEditor\FFXIII2SaveEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false
```

## PS3 save notes

- Keep a backup of the original save before editing.
- The RP mapping above was verified against a real save where the Golden Chocobo showed **RP 25** in-game.
- `0x10CDC` contained BE16 `00 19`, which is decimal 25.
- The editor is intended for decrypted save data supported by its existing save-loading workflow.

## Supported regions

| Title ID | Region | Load | Write |
|---|---|---|---|
| BLES01269 | Europe | Yes | Yes (tested) |
| BLUS30776 | USA | Yes | Yes (same APP.DAT layout as EUR, verified on a real save) |
| BCAS20224 | Asia | Yes | Read-only |

BLUS30776 uses its own secure-file ID (`20CCC79F1C3B8B2491475351C486B447`, same as Apollo's `games.conf`). Every APP.DAT offset in the reference BLUS30776 / BLES01269 save patches is identical, so the EUR field mapping applies unchanged.

The region is read from `PARAM.SFO` first, then from the folder name. If neither identifies it, every built-in key is tried and the one that correctly decrypts APP.DAT sets the region.

## PARAM.PFD

On FFXIII-2 saves, PARAM.PFD protects `PARAM.SFO` and `KEY.DAT` only, not `APP.DAT`, so **Save In Place** works directly and no external re-signing is needed.
If a save lists `APP.DAT` in PARAM.PFD, Save In Place re-signs PARAM.PFD automatically (`PfdSigner.cs`, port of pfdtool / Apollo): the original PFD must validate first, the new one is self-validated, both files are backed up and rolled back on any failure. Re-signing a real BLUS30776 PARAM.PFD with unchanged files reproduces it byte-for-byte.

## Language

The UI is available in English and French (`Assets/lang_fr.tsv`). **Auto** follows the language the game wrote in the save's `PARAM.SFO` ("Temps de jeu" → French), otherwise the Windows language. Ability, monster and role names are also shown in French (`Assets/names_fr.tsv`, display only: editor logic keeps the English names). French names come from final-fantasy.ch, matched to English names by identical learn levels / role / max level (Final Fantasy Wiki); unmatched names stay in English.

## Other additions

- **Optimize Monster**: role-based passive preset + recommended active skills in one step (stats unchanged, one undo).
- **Role Preset (Auto)** for active skills: adds the missing same-role abilities (hidden and special ones first) into empty slots, never removes anything.
- **Change summary** shown before any write (Gil, casino, characters, fragments, items, monster stats / skills / passives).
- DLC recruits identified: Omega `k952`, Lightning (Ravager) `k969`, Ultros `k951`, PuPu `k954`, Valfodr `k959` (verified on a real save), Typhon `k950`, Gilgamesh `k953`, Nabaat `k961`, Amodar `k962`, Snow `k973`, Sazh `k974` (BtEtta FFXIII-2 save editor ID table), plus `k094` Goblin.
- New abilities: Heat Blitz `at620_10`, Deathblow `ade710_00`, Dispel II `mg240_at`, Heavy Dispelga `mg250_at`.
- Fragment count read from French / Spanish / Italian / German saves.
- Automatic UI scaling with Windows DPI and window size; Ctrl + wheel / Ctrl +/- / Ctrl+0 zoom (remembered).
- Startup fix: the missing `icon.ico` / `header.png` no longer cause a `XamlParseException`.

## Version history

### v1.9.33

- Added direct Chocobo Racing RP editing.
- Added the verified RP range 0–600.
- Updated Max Racing Stats / God Chocobo to write the real RP value.
- Fixed the CoreCheck `CS0411` build issue caused by `uint` monster level-cap dictionary keys.
- Published a self-contained Windows x64 build and matching source package.

---

Final Fantasy XIII-2 and PlayStation are trademarks of their respective owners. This is an unofficial save-editing utility.
