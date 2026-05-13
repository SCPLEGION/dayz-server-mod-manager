# DayZ Server Mod Manager (Steam Workshop IDs)

WPF Windows GUI app (3 tabs) that stores Steam Workshop *published file IDs* in `mods.txt` (one ID per line).

## Build / publish

From the repo root:
```powershell
dotnet publish -c Release -o .\DayZModManager\publish
```

The runnable exe is at:
```text
DayZModManager/publish/DayZModManager.exe
```

## Usage

`mods.txt` is written next to the exe (in `DayZModManager/publish/`).

```powershell
.\DayZModManager.exe
```

The app also supports a CLI mode (same exe) for generating a merged `types.xml` from your mod folders (see below).

## Steam Workshop title lookup

Enable **Lookup titles** and set the Steam Web API key (`STEAM_API_KEY` env var) or paste it into the textbox. Then click **Refresh**.

## Two Tabs

- **Local Mods**: loads IDs from a file you pick, shows them, and lets you remove selected IDs (writes back to that same local file).
- **Add to mods.txt**: uses **Search Workshop** to search Steam, then select a result to add its published file ID into `mods.txt`.
  - If **Auto-add dependencies** is checked, it pulls required dependencies from Steam and writes *all* of them into `mods.txt` (deduped).
  - Title/description lookup uses the Steam Web API key (`STEAM_API_KEY` env var or baked-in key in the app).

- **Mods Folders**: choose a mods root folder, browse modId subfolders, and generate merged `types.xml`.

## Generate `types.xml` (merge per-mod types)

There’s a CLI mode baked into the same exe:
```powershell
.\DayZModManager.exe generate-types <modsRootDir> [outFile]
```

If you omit args, it defaults to:
- `modsRootDir = ..` (one folder up from the exe)
- `outFile = ./types.xml` (same folder as the exe)

It scans each subfolder (named by modId) for:
- `types.xml`
- any `*_types.xml` file (so `Morty_types.xml` is included)
and merges all `<types>` children into a single `types.xml`.

In the GUI tab you can also generate the merged `types.xml` using the same logic.

In **Mods Folders**, there is also:
- **Combine all mods into one types.xml** checkbox
- Output file textbox (default `types.xml` under your selected mods root)

## Integration with `servermanager.ps1`

If your DayZ server uses a `server-manager` folder with `servermanager.ps1`, put the published exe into that same folder:

- `server-manager/DayZModManager.exe`

Then in `servermanager.ps1`, run the generator before starting/restarting the server. Example:

```powershell
$modsRoot = Join-Path $PSScriptRoot "MODS_ROOT_HERE"   # folder that contains modId subfolders
$typesOut = Join-Path $PSScriptRoot "types.xml"         # written next to servermanager.ps1 / exe

& (Join-Path $PSScriptRoot "DayZModManager.exe") generate-types $modsRoot $typesOut
```

Notes:
- `$PSScriptRoot` points to the folder where `servermanager.ps1` lives.
- `MODS_ROOT_HERE` should be the folder that contains subfolders named like `2629595854` (modId folders) that have `types.xml` / `Morty_types.xml` etc.
