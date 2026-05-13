using System;
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace DayZModManager;

internal static class HistoryLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string HistoryPath => Path.Combine(AppContext.BaseDirectory, "mods_history.jsonl");

    public sealed record Entry(
        DateTimeOffset Timestamp,
        string Action,
        List<ulong> Added,
        List<ulong> Removed,
        string Source
    );

    public static void Append(string action, IEnumerable<ulong> added, IEnumerable<ulong> removed, string source)
    {
        var entry = new Entry(
            Timestamp: DateTimeOffset.UtcNow,
            Action: action,
            Added: added.ToList(),
            Removed: removed.ToList(),
            Source: source
        );

        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.AppendAllText(HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
    }

    public static List<Entry> LoadRecent(int count)
    {
        if (!File.Exists(HistoryPath))
            return new List<Entry>();

        var lines = File.ReadAllLines(HistoryPath);
        var take = lines.Reverse().Take(count);
        var result = new List<Entry>();
        foreach (var line in take)
        {
            try
            {
                var e = JsonSerializer.Deserialize<Entry>(line, JsonOptions);
                if (e != null) result.Add(e);
            }
            catch { /* ignore */ }
        }

        result.Reverse();
        return result;
    }
}
