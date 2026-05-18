using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DayZModManager.Services;
using Microsoft.Data.Sqlite;

namespace DayZModManager;

/// <summary>
/// Workshop-ID list, backed by the <c>mods</c> table in <see cref="Database"/>. The
/// <c>modsPath</c> parameter on each method is kept for API compatibility with existing
/// callers; it's used purely to write a one-way <c>mods.txt</c> mirror so external scripts
/// (e.g. <c>servermanager.ps1</c>) keep working. The DB is the authoritative source.
/// </summary>
internal static class ModStorage
{
    public sealed record LoadIdsResult(HashSet<ulong> Ids, List<string> InvalidLines);

    public static HashSet<ulong> LoadIds(string modsPath)
    {
        return LoadIdsWithValidation(modsPath).Ids;
    }

    public static LoadIdsResult LoadIdsWithValidation(string modsPath)
    {
        var set = LoadAllFromDb();
        // InvalidLines stays empty — the DB schema doesn't admit garbage rows. The shape is
        // kept so the call site (which surfaces invalid txt lines in the UI) compiles.
        return new LoadIdsResult(set, new List<string>());
    }

    public static void AddIds(string modsPath, IEnumerable<string> ids)
    {
        AddIdsAndReturnAdded(modsPath, ids);
    }

    public static HashSet<ulong> AddIdsAndReturnAdded(string modsPath, IEnumerable<string> ids)
    {
        var added = new HashSet<ulong>();
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO mods (workshop_id) VALUES ($id)";
        var p = cmd.CreateParameter(); p.ParameterName = "$id"; cmd.Parameters.Add(p);

        foreach (var idStr in ids)
        {
            var id = ParseWorkshopId(idStr);
            p.Value = (long)id;
            if (cmd.ExecuteNonQuery() > 0) added.Add(id);
        }
        tx.Commit();

        if (added.Count > 0) ExportMirror(conn, modsPath);
        return added;
    }

    public static bool RemoveId(string modsPath, ulong id)
    {
        return RemoveIdsAndReturnRemoved(modsPath, new[] { id }).Count == 1;
    }

    public static HashSet<ulong> RemoveIdsAndReturnRemoved(string modsPath, IEnumerable<ulong> ids)
    {
        var removed = new HashSet<ulong>();
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM mods WHERE workshop_id = $id";
        var p = cmd.CreateParameter(); p.ParameterName = "$id"; cmd.Parameters.Add(p);
        foreach (var id in ids)
        {
            p.Value = (long)id;
            if (cmd.ExecuteNonQuery() > 0) removed.Add(id);
        }
        tx.Commit();

        if (removed.Count > 0) ExportMirror(conn, modsPath);
        return removed;
    }

    public static void SaveIdsFromSet(string modsPath, IEnumerable<ulong> ids)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM mods";
            clear.ExecuteNonQuery();
        }
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO mods (workshop_id) VALUES ($id)";
            var p = insert.CreateParameter(); p.ParameterName = "$id"; insert.Parameters.Add(p);
            foreach (var id in ids)
            {
                p.Value = (long)id;
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();

        ExportMirror(conn, modsPath);
    }

    public static ulong ParseWorkshopId(string s)
    {
        if (!ulong.TryParse(s, out var id) || id == 0)
            throw new FormatException($"Expected a non-zero integer Workshop ID, got '{s}'.");
        return id;
    }

    // ---- internals ----

    private static HashSet<ulong> LoadAllFromDb()
    {
        var set = new HashSet<ulong>();
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT workshop_id FROM mods ORDER BY workshop_id";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            set.Add((ulong)rd.GetInt64(0));
        return set;
    }

    private static void ExportMirror(SqliteConnection conn, string modsPath)
    {
        if (string.IsNullOrWhiteSpace(modsPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(modsPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT workshop_id FROM mods ORDER BY workshop_id";
            var lines = new List<string>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) lines.Add(((ulong)rd.GetInt64(0)).ToString());

            File.WriteAllLines(modsPath, lines, Encoding.UTF8);
        }
        catch
        {
            // Mirror is a best-effort export for external tools. DB is the source of truth.
        }
    }
}
