using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DayZModManager.Services;

internal static class ServerHistoryLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string HistoryPath => Path.Combine(AppContext.BaseDirectory, "server_history.jsonl");

    public sealed record ServerEvent(
        DateTimeOffset Timestamp,
        string Action,
        string Mode,
        int? Pid,
        int? ExitCode,
        string? Detail
    );

    public static void Append(string action, string mode, int? pid = null, int? exitCode = null, string? detail = null)
    {
        var entry = new ServerEvent(DateTimeOffset.UtcNow, action, mode, pid, exitCode, detail);
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.AppendAllText(HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
    }

    public static List<ServerEvent> LoadRecent(int count)
    {
        if (!File.Exists(HistoryPath))
            return new List<ServerEvent>();

        var lines = File.ReadAllLines(HistoryPath);
        var take = lines.Reverse().Take(count);
        var result = new List<ServerEvent>();
        foreach (var line in take)
        {
            try
            {
                var e = JsonSerializer.Deserialize<ServerEvent>(line, JsonOptions);
                if (e != null) result.Add(e);
            }
            catch { /* ignore malformed lines */ }
        }
        result.Reverse();
        return result;
    }
}
