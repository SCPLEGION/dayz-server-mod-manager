using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// Lightweight embedded HTTP listener that ingests EconomySnapshot JSON from the DayZ server mod.
/// Endpoint: POST http://localhost:{port}/api/ingest with Authorization: Bearer {secret}.
/// </summary>
public sealed class EconomyApiListener : IDisposable
{
    private const int MaxSnapshotFiles = 100;
    private const int InMemoryBufferSize = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly ConcurrentQueue<EconomySnapshot> _ringBuffer = new();

    public event EventHandler<EconomySnapshot>? SnapshotReceived;
    public event EventHandler<string>? LogMessage;

    public bool IsListening { get; private set; }
    public int Port { get; private set; }
    public string Secret { get; private set; } = string.Empty;
    public DateTime? LastReceivedUtc { get; private set; }

    public static string SnapshotsDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DayZModManager", "snapshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public IReadOnlyList<EconomySnapshot> RecentSnapshots
    {
        get
        {
            lock (_gate)
                return _ringBuffer.ToArray();
        }
    }

    public void LoadRecentFromDisk()
    {
        // Backed by SQLite (economy_snapshots). Method name kept for caller compatibility.
        try
        {
            using var conn = Database.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT json_blob FROM economy_snapshots
                                ORDER BY timestamp_unix DESC
                                LIMIT $n";
            cmd.Parameters.AddWithValue("$n", InMemoryBufferSize);

            var blobs = new List<string>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) blobs.Add(rd.GetString(0));

            // Reverse so we replay oldest→newest into the ring buffer.
            blobs.Reverse();
            foreach (var blob in blobs)
            {
                try
                {
                    var snap = JsonSerializer.Deserialize<EconomySnapshot>(blob, JsonOptions);
                    if (snap != null) AppendToBuffer(snap);
                }
                catch { /* skip corrupt row */ }
            }
        }
        catch { /* db issues — buffer stays empty */ }
    }

    public void Start(int port, string secret)
    {
        lock (_gate)
        {
            if (IsListening) return;

            Port = port;
            Secret = secret ?? string.Empty;

            _listener = new HttpListener();
            var prefix = $"http://127.0.0.1:{port}/api/ingest/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));

            IsListening = true;
            Log($"Listening on http://127.0.0.1:{port}/api/ingest");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsListening) return;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _cts = null;
            _loop = null;
            IsListening = false;
            Log("Listener stopped.");
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break; // stopped
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(ctx), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!HttpMethod.Post.Method.Equals(ctx.Request.HttpMethod, StringComparison.OrdinalIgnoreCase))
            {
                await WriteStatusAsync(ctx, HttpStatusCode.MethodNotAllowed, "method not allowed");
                return;
            }

            var auth = ctx.Request.Headers["Authorization"];
            var expected = "Bearer " + Secret;
            if (string.IsNullOrEmpty(Secret) || auth != expected)
            {
                Log($"Rejected request from {ctx.Request.RemoteEndPoint}: bad auth");
                await WriteStatusAsync(ctx, HttpStatusCode.Unauthorized, "unauthorized");
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            EconomySnapshot? snap;
            try
            {
                snap = JsonSerializer.Deserialize<EconomySnapshot>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                Log("JSON parse error: " + ex.Message);
                await WriteStatusAsync(ctx, HttpStatusCode.BadRequest, "bad json");
                return;
            }

            if (snap == null)
            {
                await WriteStatusAsync(ctx, HttpStatusCode.BadRequest, "empty");
                return;
            }

            if (snap.Timestamp <= 0)
                snap.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            AppendToBuffer(snap);
            LastReceivedUtc = DateTime.UtcNow;
            PersistSnapshot(snap);

            Log($"Snapshot received: {snap.Items?.Count ?? 0} items, {snap.PlayersOnline}/{snap.PlayersMax} players.");
            SnapshotReceived?.Invoke(this, snap);

            await WriteStatusAsync(ctx, HttpStatusCode.OK, "ok");
        }
        catch (Exception ex)
        {
            Log("Handler error: " + ex.Message);
            try { await WriteStatusAsync(ctx, HttpStatusCode.InternalServerError, "error"); } catch { }
        }
    }

    private static async Task WriteStatusAsync(HttpListenerContext ctx, HttpStatusCode code, string msg)
    {
        ctx.Response.StatusCode = (int)code;
        ctx.Response.ContentType = "text/plain";
        var bytes = Encoding.UTF8.GetBytes(msg);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private void AppendToBuffer(EconomySnapshot snap)
    {
        _ringBuffer.Enqueue(snap);
        while (_ringBuffer.Count > InMemoryBufferSize && _ringBuffer.TryDequeue(out _)) { }
    }

    private void PersistSnapshot(EconomySnapshot snap)
    {
        try
        {
            var blob = JsonSerializer.Serialize(snap, JsonOptions);
            using var conn = Database.Open();
            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = @"INSERT OR REPLACE INTO economy_snapshots
                                    (timestamp_unix, json_blob)
                                    VALUES ($ts, $b)";
                ins.Parameters.AddWithValue("$ts", snap.Timestamp);
                ins.Parameters.AddWithValue("$b", blob);
                ins.ExecuteNonQuery();
            }
            // Prune to MaxSnapshotFiles rows, oldest first.
            using (var prune = conn.CreateCommand())
            {
                prune.CommandText = @"DELETE FROM economy_snapshots
                    WHERE timestamp_unix IN (
                        SELECT timestamp_unix FROM economy_snapshots
                        ORDER BY timestamp_unix DESC
                        LIMIT -1 OFFSET $keep
                    )";
                prune.Parameters.AddWithValue("$keep", MaxSnapshotFiles);
                prune.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Log("Persist error: " + ex.Message);
        }
    }

    private void Log(string msg) => LogMessage?.Invoke(this, msg);

    public void Dispose()
    {
        try { Stop(); } catch { }
    }
}
