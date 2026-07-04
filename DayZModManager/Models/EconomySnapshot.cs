using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DayZModManager.Models;

public class EconomySnapshot
{
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("playersOnline")] public int PlayersOnline { get; set; }
    [JsonPropertyName("playersMax")] public int PlayersMax { get; set; }
    [JsonPropertyName("serverUptime")] public int ServerUptime { get; set; }
    [JsonPropertyName("items")] public List<ItemEconomy> Items { get; set; } = new();
    [JsonPropertyName("zombies")] public ZombieEconomy? Zombies { get; set; }
    [JsonPropertyName("animals")] public AnimalEconomy? Animals { get; set; }
}

public class ItemEconomy
{
    [JsonPropertyName("className")] public string ClassName { get; set; } = string.Empty;
    [JsonPropertyName("spawnedCount")] public int SpawnedCount { get; set; }
    [JsonPropertyName("nominal")] public int Nominal { get; set; }
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("lifetime")] public int Lifetime { get; set; }
    [JsonPropertyName("cost")] public int Cost { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("usages")] public List<string> Usages { get; set; } = new();
    [JsonPropertyName("values")] public List<string> Values { get; set; } = new();
    [JsonPropertyName("flags")] public ItemFlags Flags { get; set; } = new();
}

public class ItemFlags
{
    [JsonPropertyName("crafted")] public bool Crafted { get; set; }
    [JsonPropertyName("deloot")] public bool Deloot { get; set; }
    [JsonPropertyName("countInMap")] public bool CountInMap { get; set; }
    [JsonPropertyName("countInCargo")] public bool CountInCargo { get; set; }
    [JsonPropertyName("countInHoarder")] public bool CountInHoarder { get; set; }
    [JsonPropertyName("countInPlayer")] public bool CountInPlayer { get; set; }
}

public class ZombieEconomy
{
    [JsonPropertyName("totalAlive")] public int TotalAlive { get; set; }
    [JsonPropertyName("totalMax")] public int TotalMax { get; set; }
    [JsonPropertyName("typeBreakdown")] public List<ZombieTypeBreakdown> TypeBreakdown { get; set; } = new();
}

public class ZombieTypeBreakdown
{
    /// <summary>
    /// Resolved events.xml &lt;event name&gt; this class spawns under, when the mod could find one
    /// (falls back to the class name itself if no matching event was found).
    /// </summary>
    [JsonPropertyName("eventName")] public string EventName { get; set; } = string.Empty;
    [JsonPropertyName("className")] public string ClassName { get; set; } = string.Empty;
    [JsonPropertyName("alive")] public int Alive { get; set; }
    [JsonPropertyName("nominal")] public int Nominal { get; set; }
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
    [JsonPropertyName("lifetime")] public int Lifetime { get; set; }
}

public class AnimalEconomy
{
    [JsonPropertyName("totalAlive")] public int TotalAlive { get; set; }
    [JsonPropertyName("typeBreakdown")] public List<AnimalTypeBreakdown> TypeBreakdown { get; set; } = new();
}

public class AnimalTypeBreakdown
{
    [JsonPropertyName("eventName")] public string EventName { get; set; } = string.Empty;
    [JsonPropertyName("className")] public string ClassName { get; set; } = string.Empty;
    [JsonPropertyName("alive")] public int Alive { get; set; }
    [JsonPropertyName("nominal")] public int Nominal { get; set; }
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
    [JsonPropertyName("lifetime")] public int Lifetime { get; set; }
}
