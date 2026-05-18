using System;
using System.Collections.Generic;

namespace DayZModManager.Services;

/// <summary>
/// Server start/stop/crash/auto-restart events, backed by the <c>server_events</c> table.
/// </summary>
internal static class ServerHistoryLogger
{
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
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO server_events
            (timestamp_utc, action, mode, pid, exit_code, detail)
            VALUES ($t,$a,$mo,$p,$ec,$d)";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$mo", mode);
        cmd.Parameters.AddWithValue("$p", (object?)pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ec", (object?)exitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static List<ServerEvent> LoadRecent(int count)
    {
        var rows = new List<ServerEvent>();
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT timestamp_utc, action, mode, pid, exit_code, detail
                            FROM server_events
                            ORDER BY id DESC
                            LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);

        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (!DateTimeOffset.TryParse(rd.GetString(0), out var ts))
                ts = DateTimeOffset.UtcNow;
            int? pid = rd.IsDBNull(3) ? null : rd.GetInt32(3);
            int? ec  = rd.IsDBNull(4) ? null : rd.GetInt32(4);
            string? detail = rd.IsDBNull(5) ? null : rd.GetString(5);
            rows.Add(new ServerEvent(ts, rd.GetString(1), rd.GetString(2), pid, ec, detail));
        }
        rows.Reverse();
        return rows;
    }
}
