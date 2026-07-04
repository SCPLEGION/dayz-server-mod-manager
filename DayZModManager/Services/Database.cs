using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DayZModManager.Services;

/// <summary>
/// Single-process SQLite store backing the manager. The file (<see cref="DbPath"/>) lives next
/// to the exe. <see cref="Open"/> returns a fresh connection — short-lived per call site is the
/// recommended pattern with Microsoft.Data.Sqlite (the connection-pool is built-in).
///
/// <para>
/// Schema is created idempotently on first call. Legacy on-disk artefacts (mods.txt, *.jsonl,
/// config.json, snapshots/*.json) are imported once and then ignored; we never delete them so
/// users can sanity-check the migration.
/// </para>
/// </summary>
internal static class Database
{
    private static readonly object InitGate = new();
    private static volatile bool _initialized;

    public static string DbPath => Path.Combine(AppContext.BaseDirectory, "dayzmm.db");

    public static string ConnectionString => $"Data Source={DbPath};Foreign Keys=True;";

    public static SqliteConnection Open()
    {
        EnsureInitialized();
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;

            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            ApplySchema(conn);

            try { Migrations.MigrateLegacy(conn); }
            catch (Exception ex)
            {
                // Migration is best-effort; the DB is still usable. Log to stderr because the
                // UI may not be up yet at first-run time.
                Console.Error.WriteLine($"[Database] legacy migration failed: {ex.Message}");
            }

            _initialized = true;
        }
    }

    private static void ApplySchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;

            CREATE TABLE IF NOT EXISTS mods (
                workshop_id   INTEGER PRIMARY KEY,
                added_at_utc  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS mod_events (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                action        TEXT NOT NULL,
                added_json    TEXT NOT NULL,
                removed_json  TEXT NOT NULL,
                source        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_mod_events_ts ON mod_events(timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS server_events (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                action        TEXT NOT NULL,
                mode          TEXT NOT NULL,
                pid           INTEGER NULL,
                exit_code     INTEGER NULL,
                detail        TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_server_events_ts ON server_events(timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS app_config (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                json_blob   TEXT NOT NULL,
                updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS economy_snapshots (
                timestamp_unix INTEGER PRIMARY KEY,
                received_utc   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                json_blob      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_economy_snapshots_recv ON economy_snapshots(received_utc DESC);

            CREATE TABLE IF NOT EXISTS balance_suggestions (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                class_name    TEXT NOT NULL,
                category      TEXT NULL,
                changes_json  TEXT NOT NULL,
                ai_reason     TEXT NULL,
                approved      INTEGER NOT NULL DEFAULT 1,
                applied_utc   TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_balance_suggestions_created ON balance_suggestions(created_utc DESC);

            CREATE TABLE IF NOT EXISTS task_proposals (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc   TEXT NOT NULL,
                title         TEXT NOT NULL,
                notes         TEXT NULL,
                tokens_used   INTEGER NOT NULL DEFAULT 0,
                actions_json  TEXT NOT NULL,
                applied_utc   TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_task_proposals_created ON task_proposals(created_utc DESC);
        ";
        cmd.ExecuteNonQuery();
    }
}
