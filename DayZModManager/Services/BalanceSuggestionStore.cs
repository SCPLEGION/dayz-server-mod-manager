using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// CRUD for the <c>balance_suggestions</c> table. Needed because MCP tool calls are independent
/// JSON-RPC turns - propose_balance and apply_balance are separate calls with nothing but the
/// DB carrying suggestion state between them.
/// </summary>
internal static class BalanceSuggestionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record StoredSuggestion(long Id, BalanceSuggestion Suggestion, DateTimeOffset? AppliedUtc);

    public static List<long> Insert(IEnumerable<BalanceSuggestion> suggestions)
    {
        var ids = new List<long>();
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();

        foreach (var s in suggestions)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO balance_suggestions
                (class_name, category, changes_json, ai_reason, approved, target_kind, event_name)
                VALUES ($cn,$cat,$ch,$ar,$ap,$tk,$en)";
            cmd.Parameters.AddWithValue("$cn", s.ClassName);
            cmd.Parameters.AddWithValue("$cat", (object?)s.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch", JsonSerializer.Serialize(s.Changes, JsonOptions));
            cmd.Parameters.AddWithValue("$ar", (object?)s.AiReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ap", s.IsApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("$tk", s.Target == SuggestionTarget.EventsXml ? "events" : "types");
            cmd.Parameters.AddWithValue("$en", (object?)s.EventName ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            ids.Add(conn.LastInsertRowId);
        }

        tx.Commit();
        return ids;
    }

    public static List<StoredSuggestion> LoadByIds(IEnumerable<long> ids)
    {
        var result = new List<StoredSuggestion>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;

        using var conn = Database.Open();
        foreach (var id in idList)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT class_name, category, changes_json, ai_reason, approved, target_kind, event_name, applied_utc
                                FROM balance_suggestions WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) continue;

            var sug = new BalanceSuggestion
            {
                ClassName = rd.GetString(0),
                Category = rd.IsDBNull(1) ? null : rd.GetString(1),
                Changes = JsonSerializer.Deserialize<Dictionary<string, FieldChange>>(rd.GetString(2), JsonOptions) ?? new(),
                AiReason = rd.IsDBNull(3) ? null : rd.GetString(3),
                IsApproved = rd.GetInt64(4) != 0,
                Target = string.Equals(rd.GetString(5), "events", StringComparison.OrdinalIgnoreCase)
                    ? SuggestionTarget.EventsXml : SuggestionTarget.TypesXml,
                EventName = rd.IsDBNull(6) ? null : rd.GetString(6),
            };
            var appliedUtc = rd.IsDBNull(7) ? (DateTimeOffset?)null
                : (DateTimeOffset.TryParse(rd.GetString(7), out var a) ? a : (DateTimeOffset?)null);

            result.Add(new StoredSuggestion(id, sug, appliedUtc));
        }
        return result;
    }

    public static List<long> LoadAllNotAppliedIds()
    {
        var ids = new List<long>();
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM balance_suggestions WHERE applied_utc IS NULL ORDER BY id";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) ids.Add(rd.GetInt64(0));
        return ids;
    }

    public static void MarkApplied(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE balance_suggestions SET applied_utc = $t WHERE id = $id";
        var pT = cmd.CreateParameter(); pT.ParameterName = "$t"; cmd.Parameters.Add(pT);
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var id in idList)
        {
            pT.Value = now;
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
