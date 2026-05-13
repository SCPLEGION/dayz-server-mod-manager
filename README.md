# DayZ Server Mod Manager (Steam Workshop IDs)

Windows GUI app that stores Steam Workshop *published file IDs* in `mods.txt` (one ID per line).

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

## Steam OAuth login (optional)

No OAuth in this version.

## Steam Workshop title lookup

Enable **Lookup titles** and set the Steam Web API key (`STEAM_API_KEY` env var) or paste it into the textbox. Then click **Refresh**.

## Two Tabs

- **Local Mods**: loads IDs from a file you pick, shows them, and lets you remove selected IDs (writes back to that same local file).
- **Add to mods.txt**: use **Search Workshop** to search Steam, then select a result to add its published file ID into `mods.txt`. If **Auto-add dependencies** is checked, it will also pull required dependencies and add them to `mods.txt`.
