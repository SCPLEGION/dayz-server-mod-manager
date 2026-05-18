using System.Collections.Generic;
using System.Text.Json;
using DayZModManager.Models;
using DayZModManager.Services;

namespace DayZModManager;

/// <summary>
/// Single-row app config, backed by the <c>app_config</c> table. The JSON shape is unchanged
/// from the legacy <c>config.json</c> file so we can round-trip in/out via the same DTO.
/// </summary>
internal sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public sealed class Config
    {
        public string? ModsRootPath { get; set; }
        public string? LocalModsTxtPath { get; set; }
        public string? CombineOutFileText { get; set; }
        public int? MergeModeSelectedIndex { get; set; }
        public string? SelectedPresetId { get; set; }
        /// <summary>Mod-folder names (just the leaf name, e.g. "@CF") excluded from XML auto-generation.</summary>
        public List<string>? ExcludedXmlGenDirs { get; set; }
        public ServerConfig? Server { get; set; }
        public AiBalancerConfig? AiBalancer { get; set; }
    }

    public static Config Load()
    {
        try
        {
            using var conn = Database.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json_blob FROM app_config WHERE id = 1";
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw)) return new Config();

            // The blob also carries Migrations._markers; deserialize loosely so unknown keys
            // (markers) are silently ignored.
            return JsonSerializer.Deserialize<Config>(raw, JsonOptions) ?? new Config();
        }
        catch
        {
            return new Config();
        }
    }

    public static void Save(Config config)
    {
        using var conn = Database.Open();

        // Preserve the _markers field (migration bookkeeping) that we don't want to round-trip
        // through the typed Config DTO.
        string? markersJson = null;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT json_blob FROM app_config WHERE id = 1";
            var existing = read.ExecuteScalar() as string;
            if (!string.IsNullOrEmpty(existing))
            {
                try
                {
                    using var doc = JsonDocument.Parse(existing);
                    if (doc.RootElement.TryGetProperty("_markers", out var m))
                        markersJson = m.GetRawText();
                }
                catch { }
            }
        }

        // Serialize the typed config and re-graft _markers if present.
        var typedJson = JsonSerializer.Serialize(config, JsonOptions);
        string blob = typedJson;
        if (!string.IsNullOrEmpty(markersJson))
        {
            var trimmed = typedJson.TrimEnd();
            if (trimmed.EndsWith("}"))
            {
                var head = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
                var sep = head.EndsWith("{") ? string.Empty : ",";
                blob = head + sep + "\"_markers\":" + markersJson + "}";
            }
        }

        using var upsert = conn.CreateCommand();
        upsert.CommandText = @"
            INSERT INTO app_config (id, json_blob) VALUES (1, $b)
            ON CONFLICT(id) DO UPDATE SET json_blob = excluded.json_blob,
                                          updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now');
        ";
        upsert.Parameters.AddWithValue("$b", blob);
        upsert.ExecuteNonQuery();
    }
}
