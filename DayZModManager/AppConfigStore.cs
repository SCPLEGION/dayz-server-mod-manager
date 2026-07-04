using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public sealed class ServerProfileEntry
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Default";
        public ServerConfig Server { get; set; } = new();
    }

    public sealed class Config
    {
        public string? ModsRootPath { get; set; }
        public string? LocalModsTxtPath { get; set; }
        public string? CombineOutFileText { get; set; }
        public int? MergeModeSelectedIndex { get; set; }
        public string? SelectedPresetId { get; set; }
        /// <summary>Mod-folder names (just the leaf name, e.g. "@CF") excluded from XML auto-generation.</summary>
        public List<string>? ExcludedXmlGenDirs { get; set; }

        /// <summary>
        /// Legacy single-profile server config. Only read during <see cref="EnsureServerProfiles"/>
        /// migration into <see cref="ServerProfiles"/>; new saves always go through the profile list.
        /// </summary>
        public ServerConfig? Server { get; set; }

        public List<ServerProfileEntry>? ServerProfiles { get; set; }
        public string? ActiveServerProfileId { get; set; }

        public AiBalancerConfig? AiBalancer { get; set; }

        /// <summary>
        /// Migrates the legacy single <see cref="Server"/> config into a "Default" profile the
        /// first time this is called, and makes sure <see cref="ActiveServerProfileId"/> always
        /// points at a profile that exists. Safe to call repeatedly.
        /// </summary>
        public void EnsureServerProfiles()
        {
            if (ServerProfiles == null || ServerProfiles.Count == 0)
            {
                ServerProfiles = new List<ServerProfileEntry>
                {
                    new ServerProfileEntry { Id = "default", Name = "Default", Server = Server ?? new ServerConfig() }
                };
            }

            if (string.IsNullOrWhiteSpace(ActiveServerProfileId) ||
                !ServerProfiles.Exists(p => p.Id == ActiveServerProfileId))
            {
                ActiveServerProfileId = ServerProfiles[0].Id;
            }
        }

        public ServerProfileEntry GetOrCreateActiveProfile()
        {
            EnsureServerProfiles();
            return ServerProfiles!.Find(p => p.Id == ActiveServerProfileId) ?? ServerProfiles![0];
        }
    }

    public static Config Load()
    {
        Config cfg;
        try
        {
            using var conn = Database.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json_blob FROM app_config WHERE id = 1";
            var raw = cmd.ExecuteScalar() as string;

            // The blob also carries Migrations._markers; deserialize loosely so unknown keys
            // (markers) are silently ignored.
            cfg = string.IsNullOrWhiteSpace(raw)
                ? new Config()
                : JsonSerializer.Deserialize<Config>(raw, JsonOptions) ?? new Config();
        }
        catch
        {
            cfg = new Config();
        }

        cfg.EnsureServerProfiles();
        return cfg;
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
            try
            {
                var node = JsonNode.Parse(typedJson)!.AsObject();
                node["_markers"] = JsonNode.Parse(markersJson);
                blob = node.ToJsonString(JsonOptions);
            }
            catch { /* fall back to config-only blob on parse error */ }
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
