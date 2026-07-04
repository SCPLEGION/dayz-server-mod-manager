using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DayZModManager.Models;

public enum SuggestionTarget
{
    /// <summary>Change applies to types.xml's &lt;type name=ClassName&gt; element.</summary>
    TypesXml,
    /// <summary>Change applies to events.xml's &lt;event name=EventName&gt; element (zombie/animal spawn groups).</summary>
    EventsXml,
}

public class BalanceSuggestion
{
    public string ClassName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public Dictionary<string, FieldChange> Changes { get; set; } = new();
    public string? AiReason { get; set; }
    public bool IsApproved { get; set; } = true;
    public SuggestionTarget Target { get; set; } = SuggestionTarget.TypesXml;
    /// <summary>Only set when <see cref="Target"/> is <see cref="SuggestionTarget.EventsXml"/>.</summary>
    public string? EventName { get; set; }
}

public class FieldChange
{
    public int OldValue { get; set; }
    public int NewValue { get; set; }

    [JsonIgnore] public int Delta => NewValue - OldValue;
    [JsonIgnore] public string Direction => Delta > 0 ? "\u2191" : Delta < 0 ? "\u2193" : "\u2192";
}

/// <summary>Raw AI response shape — one element per item that needs changes.</summary>
public class AiBalanceItem
{
    [JsonPropertyName("className")] public string ClassName { get; set; } = string.Empty;
    [JsonPropertyName("nominal")] public int? Nominal { get; set; }
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("cost")] public int? Cost { get; set; }
    [JsonPropertyName("lifetime")] public int? Lifetime { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>Raw AI response shape for zombie/animal spawn-group (events.xml) balancing.</summary>
public class AiSpawnGroupItem
{
    [JsonPropertyName("className")] public string ClassName { get; set; } = string.Empty;
    [JsonPropertyName("nominal")] public int? Nominal { get; set; }
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }
    [JsonPropertyName("lifetime")] public int? Lifetime { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public class ApplyResult
{
    public int Applied { get; set; }
    public int NotFound { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? BackupPath { get; set; }
}

public class BalanceHistoryEntry
{
    public DateTime Timestamp { get; set; }
    public int SuggestionCount { get; set; }
    public int AppliedCount { get; set; }
    public int RejectedCount { get; set; }
    public int TokensUsed { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public string Status { get; set; } = "Generated"; // Generated / Applied / Partially applied / Rejected
}
