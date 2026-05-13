using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DayZModManager;

internal sealed class AppProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    [JsonPropertyName("modsTxtPath")]
    public string ModsTxtPath { get; set; } = AppPaths.ModsTxtPath;

    [JsonPropertyName("modsRootPath")]
    public string ModsRootPath { get; set; } = string.Empty;

    [JsonPropertyName("combineOutFile")]
    public string CombineOutFile { get; set; } = "types.xml";

    [JsonPropertyName("mergeMode")]
    public TypesXmlMergeMode MergeMode { get; set; } = TypesXmlMergeMode.DedupeFirstByKey;

    [JsonPropertyName("autoAddDeps")]
    public bool AutoAddDeps { get; set; } = true;

    [JsonPropertyName("searchApiKey")]
    public string? SearchApiKey { get; set; }

    [JsonPropertyName("localModsApiKey")]
    public string? LocalModsApiKey { get; set; }

    [JsonPropertyName("modsIds")]
    public List<ulong> ModsIds { get; set; } = new();
}
