using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DayZModManager.Services;

/// <summary>
/// One-shot importers from the legacy file-based stores into SQLite. Each importer is gated by
/// a marker row in <c>app_config</c> so it never runs twice. Original files are left in place
/// so users can sanity-check the migration; subsequent runs read from SQLite only.
/// </summary>
internal static class Migrations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MigrateLegacy(SqliteConnection conn)
    {
        if (!HasMarker(conn, "migrated:mods.txt"))
        {
            ImportModsTxt(conn);
            SetMarker(conn, "migrated:mods.txt");
        }
        if (!HasMarker(conn, "migrated:mods_history.jsonl"))
        {
            ImportJsonl(conn, Path.Combine(AppContext.BaseDirectory, "mods_history.jsonl"),
                ImportModEventLine);
            SetMarker(conn, "migrated:mods_history.jsonl");
        }
        if (!HasMarker(conn, "migrated:server_history.jsonl"))
        {
            ImportJsonl(conn, Path.Combine(AppContext.BaseDirectory, "server_history.jsonl"),
                ImportServerEventLine);
            SetMarker(conn, "migrated:server_history.jsonl");
        }
        if (!HasMarker(conn, "migrated:config.json"))
        {
            ImportConfigJson(conn);
            SetMarker(conn, "migrated:config.json");
        }
        if (!HasMarker(conn, "migrated:snapshots"))
        {
            ImportSnapshotFolder(conn);
            SetMarker(conn, "migrated:snapshots");
        }
        if (!HasMarker(conn, "schema:balance_suggestions.target_kind"))
        {
            AddBalanceSuggestionTargetColumns(conn);
            SetMarker(conn, "schema:balance_suggestions.target_kind");
        }
    }

    // ---- schema migrations (ALTER TABLE for columns added after a table already shipped) ----

    private static void AddBalanceSuggestionTargetColumns(SqliteConnection conn)
    {
        // Database.ApplySchema already creates these columns on a fresh install; these ALTERs
        // only do real work against a pre-existing balance_suggestions table from before this
        // feature shipped. "duplicate column" means a fresh CREATE TABLE already has it - fine.
        ExecuteIgnoringDuplicateColumn(conn, "ALTER TABLE balance_suggestions ADD COLUMN target_kind TEXT NOT NULL DEFAULT 'types'");
        ExecuteIgnoringDuplicateColumn(conn, "ALTER TABLE balance_suggestions ADD COLUMN event_name TEXT NULL");
    }

    private static void ExecuteIgnoringDuplicateColumn(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.IndexOf("duplicate column", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Column already present - nothing to do.
        }
    }

    // ---- markers stored in app_config as JSON of {"_markers": {...}} merged with config blob ----

    private static bool HasMarker(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json_blob FROM app_config WHERE id = 1";
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("_markers", out var m)) return false;
            return m.TryGetProperty(key, out _);
        }
        catch { return false; }
    }

    private static void SetMarker(SqliteConnection conn, string key)
    {
        Dictionary<string, object?> root;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT json_blob FROM app_config WHERE id = 1";
            var raw = read.ExecuteScalar() as string;
            root = string.IsNullOrEmpty(raw)
                ? new Dictionary<string, object?>()
                : (JsonSerializer.Deserialize<Dictionary<string, object?>>(raw!, Json) ?? new());
        }

        var markersJson = root.TryGetValue("_markers", out var m) && m != null
            ? JsonSerializer.Serialize(m, Json)
            : "{}";
        var markers = JsonSerializer.Deserialize<Dictionary<string, string>>(markersJson, Json) ?? new();
        markers[key] = DateTimeOffset.UtcNow.ToString("O");
        root["_markers"] = markers;

        var blob = JsonSerializer.Serialize(root, Json);
        using var upsert = conn.CreateCommand();
        upsert.CommandText = @"
            INSERT INTO app_config (id, json_blob) VALUES (1, $b)
            ON CONFLICT(id) DO UPDATE SET json_blob = excluded.json_blob,
                                          updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now');
        ";
        upsert.Parameters.AddWithValue("$b", blob);
        upsert.ExecuteNonQuery();
    }

    // ---- importers ----

    private static void ImportModsTxt(SqliteConnection conn)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "mods.txt");
        if (!File.Exists(path)) return;

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO mods (workshop_id) VALUES ($id)";
        var p = cmd.CreateParameter(); p.ParameterName = "$id"; cmd.Parameters.Add(p);
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (!ulong.TryParse(t, out var id) || id == 0) continue;
            p.Value = (long)id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void ImportJsonl(SqliteConnection conn, string path,
        Action<SqliteConnection, JsonDocument> handler)
    {
        if (!File.Exists(path)) return;
        using var tx = conn.BeginTransaction();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                handler(conn, doc);
            }
            catch { /* skip bad line */ }
        }
        tx.Commit();
    }

    private static void ImportModEventLine(SqliteConnection conn, JsonDocument doc)
    {
        var root = doc.RootElement;
        var ts     = root.TryGetProperty("timestamp", out var t) ? t.GetString() ?? DateTimeOffset.UtcNow.ToString("O") : DateTimeOffset.UtcNow.ToString("O");
        var action = root.TryGetProperty("action",    out var a) ? a.GetString() ?? string.Empty : string.Empty;
        var added  = root.TryGetProperty("added",     out var ad) ? ad.GetRawText() : "[]";
        var rem    = root.TryGetProperty("removed",   out var rm) ? rm.GetRawText() : "[]";
        var src    = root.TryGetProperty("source",    out var s) ? s.GetString() ?? string.Empty : string.Empty;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO mod_events
            (timestamp_utc, action, added_json, removed_json, source)
            VALUES ($t,$a,$ad,$rm,$s)";
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$ad", added);
        cmd.Parameters.AddWithValue("$rm", rem);
        cmd.Parameters.AddWithValue("$s", src);
        cmd.ExecuteNonQuery();
    }

    private static void ImportServerEventLine(SqliteConnection conn, JsonDocument doc)
    {
        var root = doc.RootElement;
        var ts     = root.TryGetProperty("timestamp", out var t) ? t.GetString() ?? DateTimeOffset.UtcNow.ToString("O") : DateTimeOffset.UtcNow.ToString("O");
        var action = root.TryGetProperty("action",    out var a) ? a.GetString() ?? string.Empty : string.Empty;
        var mode   = root.TryGetProperty("mode",      out var m) ? m.GetString() ?? string.Empty : string.Empty;
        int? pid   = root.TryGetProperty("pid",       out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
        int? ec    = root.TryGetProperty("exitCode",  out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : (int?)null;
        var det    = root.TryGetProperty("detail",    out var d) ? d.GetString() : null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO server_events
            (timestamp_utc, action, mode, pid, exit_code, detail)
            VALUES ($t,$a,$mo,$p,$ec,$d)";
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$mo", mode);
        cmd.Parameters.AddWithValue("$p", (object?)pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ec", (object?)ec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)det ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void ImportConfigJson(SqliteConnection conn)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(path)) return;
        var blob = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(blob)) return;

        // Preserve existing _markers if app_config already has a blob.
        Dictionary<string, object?> existing;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT json_blob FROM app_config WHERE id = 1";
            var raw = read.ExecuteScalar() as string;
            existing = string.IsNullOrEmpty(raw)
                ? new Dictionary<string, object?>()
                : (JsonSerializer.Deserialize<Dictionary<string, object?>>(raw!, Json) ?? new());
        }
        Dictionary<string, object?>? incoming = null;
        try { incoming = JsonSerializer.Deserialize<Dictionary<string, object?>>(blob, Json); }
        catch { return; }
        if (incoming == null) return;
        foreach (var kv in incoming)
            if (kv.Key != "_markers") existing[kv.Key] = kv.Value;

        var merged = JsonSerializer.Serialize(existing, Json);
        using var upsert = conn.CreateCommand();
        upsert.CommandText = @"
            INSERT INTO app_config (id, json_blob) VALUES (1, $b)
            ON CONFLICT(id) DO UPDATE SET json_blob = excluded.json_blob,
                                          updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now');
        ";
        upsert.Parameters.AddWithValue("$b", merged);
        upsert.ExecuteNonQuery();
    }

    private static void ImportSnapshotFolder(SqliteConnection conn)
    {
        string dir;
        try
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DayZModManager", "snapshots");
        }
        catch { return; }
        if (!Directory.Exists(dir)) return;

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR IGNORE INTO economy_snapshots (timestamp_unix, json_blob)
                            VALUES ($ts, $b)";
        var pts = cmd.CreateParameter(); pts.ParameterName = "$ts"; cmd.Parameters.Add(pts);
        var pb  = cmd.CreateParameter(); pb.ParameterName  = "$b";  cmd.Parameters.Add(pb);

        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var blob = File.ReadAllText(f);
                using var d = JsonDocument.Parse(blob);
                if (!d.RootElement.TryGetProperty("timestamp", out var t)) continue;
                if (t.ValueKind != JsonValueKind.Number) continue;
                pts.Value = t.GetInt64();
                pb.Value  = blob;
                cmd.ExecuteNonQuery();
            }
            catch { /* skip bad file */ }
        }
        tx.Commit();
    }
}
