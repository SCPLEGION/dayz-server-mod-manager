using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DayZModManager;

internal static class ModStorage
{
    public static HashSet<ulong> LoadIds(string modsPath)
    {
        var set = new HashSet<ulong>();

        if (!File.Exists(modsPath))
            return set;

        foreach (var line in File.ReadAllLines(modsPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            set.Add(ParseWorkshopId(trimmed));
        }

        return set;
    }

    public static void AddIds(string modsPath, IEnumerable<string> ids)
    {
        var existing = LoadIds(modsPath);
        var addedAny = false;

        foreach (var idStr in ids)
        {
            var id = ParseWorkshopId(idStr);
            if (existing.Contains(id)) continue;
            existing.Add(id);
            addedAny = true;
        }

        if (!addedAny) return;
        SaveIds(modsPath, existing);
    }

    public static bool RemoveId(string modsPath, ulong id)
    {
        var existing = LoadIds(modsPath);
        var removed = existing.Remove(id);
        if (!removed) return false;
        SaveIds(modsPath, existing);
        return true;
    }

    private static void SaveIds(string modsPath, IEnumerable<ulong> ids)
    {
        var dir = Path.GetDirectoryName(modsPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Deterministic output.
        var ordered = ids.OrderBy(x => x).Select(x => x.ToString()).ToArray();
        File.WriteAllLines(modsPath, ordered, Encoding.UTF8);
    }

    public static ulong ParseWorkshopId(string s)
    {
        if (!ulong.TryParse(s, out var id) || id == 0)
            throw new FormatException($"Expected a non-zero integer Workshop ID, got '{s}'.");
        return id;
    }
}
