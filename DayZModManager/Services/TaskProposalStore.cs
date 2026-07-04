using System;
using System.Collections.Generic;
using System.Text.Json;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// CRUD for the <c>task_proposals</c> table. Same rationale as <see cref="BalanceSuggestionStore"/>:
/// MCP's plan_task/apply_task are separate JSON-RPC calls, so the plan has to be persisted
/// between them rather than kept in memory.
/// </summary>
internal static class TaskProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static long Insert(TaskProposal proposal)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO task_proposals
            (created_utc, title, notes, tokens_used, actions_json)
            VALUES ($t,$title,$notes,$tok,$actions)";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$title", proposal.Title);
        cmd.Parameters.AddWithValue("$notes", (object?)proposal.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tok", proposal.TokensUsed);
        cmd.Parameters.AddWithValue("$actions", JsonSerializer.Serialize(proposal.Actions, JsonOptions));
        cmd.ExecuteNonQuery();
        return conn.LastInsertRowId;
    }

    public static (TaskProposal? Proposal, DateTimeOffset? AppliedUtc) LoadById(long id)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT title, notes, tokens_used, actions_json, applied_utc, created_utc
                            FROM task_proposals WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return (null, null);

        var proposal = new TaskProposal
        {
            Title = rd.GetString(0),
            Notes = rd.IsDBNull(1) ? null : rd.GetString(1),
            TokensUsed = (int)rd.GetInt64(2),
            Actions = JsonSerializer.Deserialize<List<TaskAction>>(rd.GetString(3), JsonOptions) ?? new(),
            CreatedAt = DateTimeOffset.TryParse(rd.GetString(5), out var c) ? c.LocalDateTime : DateTime.Now,
        };
        var appliedUtc = rd.IsDBNull(4) ? (DateTimeOffset?)null
            : (DateTimeOffset.TryParse(rd.GetString(4), out var a) ? a : (DateTimeOffset?)null);

        return (proposal, appliedUtc);
    }

    public static void MarkApplied(long id)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_proposals SET applied_utc = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
