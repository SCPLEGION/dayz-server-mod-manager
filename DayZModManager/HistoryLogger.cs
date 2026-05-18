using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DayZModManager.Services;

namespace DayZModManager;

/// <summary>
/// Mod add/remove history, backed by the <c>mod_events</c> table.
/// </summary>
internal static class HistoryLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record Entry(
        DateTimeOffset Timestamp,
        string Action,
        List<ulong> Added,
        List<ulong> Removed,
        string Source
    );

    public static void Append(string action, IEnumerable<ulong> added, IEnumerable<ulong> removed, string source)
    {
        var addedList = added.ToList();
        var removedList = removed.ToList();

        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO mod_events
            (timestamp_utc, action, added_json, removed_json, source)
            VALUES ($t,$a,$ad,$rm,$s)";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$ad", JsonSerializer.Serialize(addedList, JsonOptions));
        cmd.Parameters.AddWithValue("$rm", JsonSerializer.Serialize(removedList, JsonOptions));
        cmd.Parameters.AddWithValue("$s", source);
        cmd.ExecuteNonQuery();
    }

    public static List<Entry> LoadRecent(int count)
    {
        var rows = new List<Entry>();
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT timestamp_utc, action, added_json, removed_json, source
                            FROM mod_events
                            ORDER BY id DESC
                            LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);

        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var added = DeserializeIdList(rd.GetString(2));
            var removed = DeserializeIdList(rd.GetString(3));
            if (!DateTimeOffset.TryParse(rd.GetString(0), out var ts))
                ts = DateTimeOffset.UtcNow;
            rows.Add(new Entry(ts, rd.GetString(1), added, removed, rd.GetString(4)));
        }
        rows.Reverse(); // return ascending by time, same as the legacy implementation
        return rows;
    }

    private static List<ulong> DeserializeIdList(string json)
    {
        try { return JsonSerializer.Deserialize<List<ulong>>(json, JsonOptions) ?? new(); }
        catch { return new List<ulong>(); }
    }
}
