using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DayZModManager;

internal static class ModStorage
{
    public sealed record LoadIdsResult(HashSet<ulong> Ids, List<string> InvalidLines);

    public static HashSet<ulong> LoadIds(string modsPath)
    {
        return LoadIdsWithValidation(modsPath).Ids;
    }

    public static LoadIdsResult LoadIdsWithValidation(string modsPath)
    {
        var set = new HashSet<ulong>();
        var invalid = new List<string>();

        if (!File.Exists(modsPath))
            return new LoadIdsResult(set, invalid);

        foreach (var line in File.ReadAllLines(modsPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            try
            {
                set.Add(ParseWorkshopId(trimmed));
            }
            catch (FormatException)
            {
                invalid.Add(trimmed);
            }
        }

        return new LoadIdsResult(set, invalid);
    }

    public static void AddIds(string modsPath, IEnumerable<string> ids)
    {
        AddIdsAndReturnAdded(modsPath, ids);
    }

    public static HashSet<ulong> AddIdsAndReturnAdded(string modsPath, IEnumerable<string> ids)
    {
        var existing = LoadIds(modsPath);
        var added = new HashSet<ulong>();

        foreach (var idStr in ids)
        {
            var id = ParseWorkshopId(idStr);
            if (existing.Contains(id)) continue;
            existing.Add(id);
            added.Add(id);
        }

        if (added.Count == 0) return added;
        SaveIds(modsPath, existing);
        return added;
    }

    public static bool RemoveId(string modsPath, ulong id)
    {
        return RemoveIdsAndReturnRemoved(modsPath, new[] { id }).Count == 1;
    }

    public static HashSet<ulong> RemoveIdsAndReturnRemoved(string modsPath, IEnumerable<ulong> ids)
    {
        var existing = LoadIds(modsPath);
        var removed = new HashSet<ulong>();
        foreach (var id in ids)
        {
            if (existing.Remove(id))
                removed.Add(id);
        }

        if (removed.Count == 0) return removed;
        SaveIds(modsPath, existing);
        return removed;
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

    public static void SaveIdsFromSet(string modsPath, IEnumerable<ulong> ids)
    {
        SaveIds(modsPath, ids);
    }

    public static ulong ParseWorkshopId(string s)
    {
        if (!ulong.TryParse(s, out var id) || id == 0)
            throw new FormatException($"Expected a non-zero integer Workshop ID, got '{s}'.");
        return id;
    }
}
