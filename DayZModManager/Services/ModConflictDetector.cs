using System.Collections.Generic;
using System.Linq;

namespace DayZModManager.Services;

internal sealed record ModConflictReportEntry(string PresetDisplayName, XmlMergeConflict Conflict);

/// <summary>
/// Cross-preset "do any mods collide?" report, built on top of the conflict data
/// <see cref="XmlMergeService"/> already computes during a merge preview/generate. Scoped to
/// XML economy-file fragments (types/events/spawnable presets) - detecting binarized
/// config.cpp/PBO-level class overrides would need a PBO unpacker or a real Enfusion-config
/// parser, neither of which exist in this codebase.
/// </summary>
internal static class ModConflictDetector
{
    public static readonly IReadOnlyList<string> DefaultPresetIds = new[]
    {
        "types", "events", "cfgspawnabletypes", "cfgeventspawns"
    };

    public static IReadOnlyList<ModConflictReportEntry> DetectConflicts(
        string modsRoot,
        TypesXmlMergeMode mode,
        IReadOnlyCollection<string>? excludedDirNames = null,
        IReadOnlyList<string>? presetIds = null)
    {
        var ids = presetIds ?? DefaultPresetIds;
        var results = new List<ModConflictReportEntry>();

        foreach (var id in ids)
        {
            var preset = XmlMergePresets.FindById(id);
            if (preset == null) continue;

            XmlMergeStats stats;
            try { stats = XmlMergeService.Preview(modsRoot, preset, mode, excludedDirNames); }
            catch { continue; } // best-effort: a bad mods root just yields an empty report

            foreach (var conflict in stats.Conflicts)
                results.Add(new ModConflictReportEntry(preset.DisplayName, conflict));
        }

        return results;
    }

    public static string FormatReport(IReadOnlyList<ModConflictReportEntry> entries)
    {
        if (entries.Count == 0) return "No conflicts found across types/events/spawnable presets.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{entries.Count} conflict(s) found:");
        foreach (var group in entries.GroupBy(e => e.PresetDisplayName))
        {
            sb.AppendLine();
            sb.AppendLine($"// {group.Key}");
            foreach (var e in group.Take(25))
                sb.AppendLine($"  - {e.Conflict.Key}  ({e.Conflict.ExistingSource} vs {e.Conflict.NewSource})");
            if (group.Count() > 25)
                sb.AppendLine($"  ... +{group.Count() - 25} more");
        }
        return sb.ToString();
    }
}
