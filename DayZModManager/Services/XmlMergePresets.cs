using System.Collections.Generic;

namespace DayZModManager.Services;

/// <summary>
/// Built-in DayZ XML merge presets. Each preset describes which mod files to harvest and
/// how to merge their children. The patterns intentionally match the common "PREFIX_NAME.xml"
/// shape mods use (e.g. <c>1559212036_types.xml</c>, <c>Morty_types.xml</c>).
/// </summary>
internal static class XmlMergePresets
{
    /// <summary>
    /// Default dir-name exclusions for XML auto-generation. These are stock DayZ server
    /// subfolders that don't contain mod XML payloads, so scanning them only wastes time
    /// and risks false positives. Used on first run to seed the EXCLUDE DIRS textbox;
    /// users can still edit or clear it.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludedDirs = new[]
    {
        "addons",
        "appcache",
        "battleye",
        "config",
        "docs",
        "dta",
        "keys",
        "logs",
        "mpmissions",
        "sakhal",
        "server_manager",
    };

    public static readonly IReadOnlyList<XmlMergePreset> All = new[]
    {
        // ── types.xml (loot tables) ───────────────────────────────────────────
        new XmlMergePreset(
            Id: "types",
            DisplayName: "types.xml — loot tables",
            IncludePatterns: new[] { "types.xml", "*_types.xml", "*types.xml" },
            // Exclude the lookalikes that contain "types" but are unrelated
            ExcludePatterns: new[] {
                "*cfgspawnabletypes*.xml",
                "*spawnabletypes*.xml",
                "*proxytypes*.xml"
            },
            RootElementName: "types",
            KeyAttribute:   "name",
            OutputFileName: "types.xml",
            Description: "Merge <type> entries across all mods. Dedupe by name."
        ),

        // ── cfgspawnabletypes.xml (attachments / cargo / hoarder presets) ─────
        new XmlMergePreset(
            Id: "cfgspawnabletypes",
            DisplayName: "cfgspawnabletypes.xml — spawnable presets",
            IncludePatterns: new[] {
                "cfgspawnabletypes.xml",
                "*_cfgspawnabletypes.xml",
                "*cfgspawnabletypes*.xml",
                "*spawnabletypes*.xml"
            },
            ExcludePatterns: new string[0],
            RootElementName: "spawnabletypes",
            KeyAttribute:   "name",
            OutputFileName: "cfgspawnabletypes.xml",
            Description: "Merge attachment / cargo / hoarder presets for spawnable types."
        ),

        // ── events.xml (event definitions) ────────────────────────────────────
        new XmlMergePreset(
            Id: "events",
            DisplayName: "events.xml — event definitions",
            IncludePatterns: new[] { "events.xml", "*_events.xml" },
            ExcludePatterns: new[] { "*cfgeventspawns*.xml", "*eventspawns*.xml" },
            RootElementName: "events",
            KeyAttribute:   "name",
            OutputFileName: "events.xml",
            Description: "Merge <event> definitions (loot/animal/zombie events)."
        ),

        // ── cfgeventspawns.xml (event spawn positions) ────────────────────────
        new XmlMergePreset(
            Id: "cfgeventspawns",
            DisplayName: "cfgeventspawns.xml — event positions",
            IncludePatterns: new[] {
                "cfgeventspawns.xml",
                "*_cfgeventspawns.xml",
                "*cfgeventspawns*.xml"
            },
            ExcludePatterns: new string[0],
            RootElementName: "eventposdef",
            KeyAttribute:   "name",
            OutputFileName: "cfgeventspawns.xml",
            Description: "Merge <event> position groups used by events.xml."
        ),

        // ── mapgroupproto.xml (loot proxy groups / categories) ────────────────
        new XmlMergePreset(
            Id: "mapgroupproto",
            DisplayName: "mapgroupproto.xml — loot proxy groups",
            IncludePatterns: new[] { "mapgroupproto.xml", "*_mapgroupproto.xml", "*mapgroupproto*.xml" },
            ExcludePatterns: new string[0],
            RootElementName: "map",
            KeyAttribute:   "name",
            OutputFileName: "mapgroupproto.xml",
            Description: "Merge loot proxy groups (containers, lootmax, categories…)."
        ),

        // ── cfgrandompresets.xml (loot variants) ──────────────────────────────
        new XmlMergePreset(
            Id: "cfgrandompresets",
            DisplayName: "cfgrandompresets.xml — random presets",
            IncludePatterns: new[] {
                "cfgrandompresets.xml",
                "*_cfgrandompresets.xml",
                "*cfgrandompresets*.xml",
                "*randompresets*.xml"
            },
            ExcludePatterns: new string[0],
            RootElementName: "randompresets",
            KeyAttribute:   "name",
            OutputFileName: "cfgrandompresets.xml",
            Description: "Merge attachment/cargo random presets referenced by spawnable types."
        ),

        // ── cfgenvironment.xml (animals / infected) ───────────────────────────
        new XmlMergePreset(
            Id: "cfgenvironment",
            DisplayName: "cfgenvironment.xml — environment / animal spawns",
            IncludePatterns: new[] { "cfgenvironment.xml", "*_cfgenvironment.xml", "*cfgenvironment*.xml" },
            ExcludePatterns: new string[0],
            RootElementName: "env",
            KeyAttribute:   "name",
            OutputFileName: "cfgenvironment.xml",
            Description: "Merge environment / animal spawn definitions."
        ),

        // ── cfgweather.xml ────────────────────────────────────────────────────
        new XmlMergePreset(
            Id: "cfgweather",
            DisplayName: "cfgweather.xml — weather config",
            IncludePatterns: new[] { "cfgweather.xml", "*_cfgweather.xml", "*cfgweather*.xml" },
            ExcludePatterns: new string[0],
            RootElementName: "weather",
            KeyAttribute:   null, // weather typically has fixed child names, dedupe by content
            OutputFileName: "cfgweather.xml",
            Description: "Merge weather sub-elements. Children are deduped by content."
        ),

        // ── spawnabletypes.xml (legacy alias) ─────────────────────────────────
        new XmlMergePreset(
            Id: "spawnabletypes",
            DisplayName: "spawnabletypes.xml — legacy spawnable presets",
            IncludePatterns: new[] { "spawnabletypes.xml", "*_spawnabletypes.xml" },
            ExcludePatterns: new[] { "*cfgspawnabletypes*.xml" },
            RootElementName: "spawnabletypes",
            KeyAttribute:   "name",
            OutputFileName: "spawnabletypes.xml",
            Description: "Legacy spawnabletypes (older mods). Same shape as cfgspawnabletypes."
        ),

        // ── messages.xml (periodic server broadcast messages) ─────────────────
        new XmlMergePreset(
            Id: "messages",
            DisplayName: "messages.xml — server messages",
            IncludePatterns: new[] { "messages.xml", "*_messages.xml", "*messages*.xml" },
            ExcludePatterns: new string[0],
            RootElementName: "messages",
            KeyAttribute:   "name",
            OutputFileName: "messages.xml",
            Description: "Merge <message> entries (periodic server broadcasts) across mods. Dedupe by name."
        ),

        // ── custom (user-configured, no implicit patterns) ────────────────────
        new XmlMergePreset(
            Id: "custom",
            DisplayName: "custom… — define your own",
            IncludePatterns: new[] { "*.xml" },
            ExcludePatterns: new string[0],
            RootElementName: "root",
            KeyAttribute:   "name",
            OutputFileName: "merged.xml",
            Description: "Pick patterns, root element, key attribute and output filename yourself."
        ),
    };

    public static XmlMergePreset? FindById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, System.StringComparison.OrdinalIgnoreCase));
}
