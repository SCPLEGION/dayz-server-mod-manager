# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```powershell
# Build (framework-dependent; requires .NET 8 Desktop Runtime on the target machine)
dotnet build DayZModManager/DayZModManager.csproj

# Publish runnable exe next to the project
dotnet publish -c Release -o .\DayZModManager\publish

# Pack into a zip for distribution (writes to ./dist/)
pwsh ./scripts/build-and-pack.ps1

# Self-contained single-file exe
pwsh ./scripts/build-and-pack.ps1 -SelfContained -SingleFile -Version 1.0.0
```

There are no automated tests in this repository. CI (`.github/workflows/ci.yml`) only builds and packs — it does not run tests. The build target is `net8.0-windows` (WPF), so it only compiles and runs on Windows.

## Architecture

### Entry point and CLI dispatch

`App.xaml.cs` intercepts command-line arguments before showing the WPF window. Three headless CLI modes are dispatched from there:

| Command | Handler |
|---|---|
| `generate-types <modsRoot> [outFile]` | `TypesXmlGenerator` (legacy shorthand, same as XML merge) |
| `balance-suggest <typesXml> [--api-key ...]` | `Cli/BalanceSuggestCli.cs` |
| `mcp-server` | `Cli/McpServerCli.cs` |

If no recognized CLI verb is present, the WPF `MainWindow` starts normally.

### MainWindow tabs

`MainWindow.xaml/.cs` hosts six tabs and a single `AiBalancerTab` user control:

- **LOCAL_MODS** — reads/writes `mods.txt` (one Workshop `ulong` ID per line); optionally resolves titles via Steam Web API.
- **SEARCH_WORKSHOP** — queries Steam Workshop (`SteamWorkshopClient`) for mods; adds IDs to `mods.txt` with optional dependency tree resolution.
- **MOD_FOLDERS** — browses a mods root directory; drives `XmlMergeService` to merge DayZ XML files from mod subfolders.
- **SERVER** — controls the DayZ server process via `ServerProcessController`; SteamCMD mod updates via `SteamCmdClient`; RPT/ADM log tail via `RptLogTail`.
- **HISTORY** — reads from the `mod_events` table via `HistoryLogger`.
- **SETTINGS** / **AI BALANCER** — AI economy balancer (separate `AiBalancerTab` user control).

### Persistence layer

All state is stored in a SQLite database (`dayzmm.db`) next to the exe, managed by `Services/Database.cs`:

| Table | Purpose |
|---|---|
| `app_config` | Single-row JSON blob (`AppConfigStore.Config`) containing all UI settings, server config, and AI balancer config |
| `mods` | Workshop IDs currently in `mods.txt` |
| `mod_events` | History log (add/remove actions) |
| `server_events` | Server start/stop/crash log |
| `economy_snapshots` | Incoming economy data from the DayZ companion mod |
| `balance_suggestions` | AI balancer output |
| `task_proposals` | AI task proposals |

`AppConfigStore.Load/Save` round-trips the typed `Config` DTO as JSON into the single `app_config` row. It deliberately preserves an internal `_markers` key (used by `Migrations.cs` to track one-time legacy migrations from `config.json`/`mods.txt`/`*.jsonl`).

Short-lived connections are the pattern: call `Database.Open()`, use the connection, dispose. The built-in Microsoft.Data.Sqlite connection pool handles the rest.

### XML merge system

`XmlMergeService` merges DayZ server XML files (loot tables, events, etc.) from multiple mod subfolders into one output file. It is driven by `XmlMergePreset` records defined in `XmlMergePresets.cs`:

- **IncludePatterns** / **ExcludePatterns**: glob patterns matched against filenames only.
- **RootElementName**: the XML element whose children are merged (e.g., `"types"` for `types.xml`).
- **KeyAttribute**: attribute used to deduplicate children (e.g., `"name"`). When null, the full serialized element string is the key.
- **MergeMode**: `DedupeFirstByKey` (keep first), `Append` (keep all), `DedupeLastByKey` (keep last).

The built-in presets cover `types.xml`, `cfgspawnabletypes.xml`, `events.xml`, `cfgeventspawns.xml`, `mapgroupproto.xml`, `cfgrandompresets.xml`, `cfgenvironment.xml`, `cfgweather.xml`, and a user-configurable `custom` preset.

### Server management

`ServerConfig` defines two launch modes:

- **Ps1** — delegates to a user-supplied `servermanager.ps1`; the manager spawns PowerShell and streams its stdout into the log pane.
- **DirectExe** — manages `DayZServer.exe` directly; uses `SteamCmdClient` to download mods (via SteamCMD) and `DeployMods` to junction/symlink/copy them into the server root before launch.

`ServerProcessController` owns the process lifecycle (start, stop, restart, auto-restart on crash with configurable backoff and retry cap). Closing the manager window calls `Detach()` — it does not kill the server.

### AI Balancer pipeline

1. The **companion DayZ mod** (`dayz-mod/@DayZAIBalancer/`) runs server-side in Enforce Script. It collects item economy data at a configured interval and POSTs a JSON `EconomySnapshot` to the local HTTP endpoint.
2. `EconomyApiListener` (embedded `HttpListener` on `http://127.0.0.1:{port}/api/ingest`) receives snapshots, persists them to SQLite, and fires `SnapshotReceived`.
3. `AiBalancerService.RunAsync` batches items (default 30/batch), calls the OpenAI chat completions API with a structured prompt, and parses the JSON response into `BalanceSuggestion` records.
4. `XmlApplyService` / `TaskApplyService` write approved suggestions back to the server's `types.xml`.

### MCP server

`Cli/McpServerCli.cs` implements a minimal MCP stdio server (JSON-RPC 2.0 over stdin/stdout). It exposes tools for listing/reading/writing server files, getting/setting `serverDZ.cfg` keys, retrieving the latest economy snapshot, and running the AI balancer. External AI clients (e.g., Claude Desktop) can use it by launching `DayZModManager.exe mcp-server`.

### Path conventions

`AppPaths.cs` defines the canonical layout:

```
parent1/                   ← DefaultModsRoot (mod subfolders live here)
  parent2/                 ← ExeDir (AppContext.BaseDirectory)
    DayZModManager.exe
    mods.txt               ← Workshop IDs, one per line
    dayzmm.db              ← SQLite database
    config.json            ← legacy, migrated on first run
    ServerProfile/         ← default RPT log directory
    steamcmd-cache/        ← default SteamCMD install root
```

Relative output paths (e.g., `types.xml`) resolve against `ExeDir` via `AppPaths.ResolveOutputPath`.

### Companion mod (`dayz-mod/`)

The `@DayZAIBalancer` folder contains Enforce Script sources for the server-side DayZ mod. These must be packed into a PBO file (`addons/DayZAIBalancer.pbo`) using a tool like `addonbuilder` or Mikero's PBOProject before they can be loaded by a DayZ server. The `scripts/pack-mod-pbo.ps1` script assists with this. The mod depends on Community-Framework (CF) for its `RestContext` HTTP class.
