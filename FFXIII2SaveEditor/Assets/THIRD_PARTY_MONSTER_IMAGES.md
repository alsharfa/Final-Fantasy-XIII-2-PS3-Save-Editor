# Optional real monster image pack

The editor can use real Final Fantasy XIII-2 enemy images downloaded from the Final Fantasy Wiki / Fandom category:

`https://finalfantasy.fandom.com/wiki/Category:Final_Fantasy_XIII-2_enemy_images`

Run `download-real-monster-images.bat` at the repository root. The script uses the MediaWiki API to enumerate that category, matches image filenames to the editor's Paradigm Pack monster IDs, downloads a local copy under `Assets/monster_real/`, and writes `monster_real_manifest.tsv` with the source file/URL for each match.

The downloaded third-party images are intentionally not bundled in this source ZIP. Image licensing can vary by file even when the surrounding wiki text is CC-BY-SA. Keep the generated manifest with any local image pack so individual sources can be audited. The editor falls back to its built-in generated monster icon for unmatched monsters.
