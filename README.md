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

## Version history

### v1.9.33

- Added direct Chocobo Racing RP editing.
- Added the verified RP range 0–600.
- Updated Max Racing Stats / God Chocobo to write the real RP value.
- Fixed the CoreCheck `CS0411` build issue caused by `uint` monster level-cap dictionary keys.
- Published a self-contained Windows x64 build and matching source package.

---

Final Fantasy XIII-2 and PlayStation are trademarks of their respective owners. This is an unofficial save-editing utility.
