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
        try
        {
            var dir = SnapshotsDir;
            var files = new DirectoryInfo(dir)
                .GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(InMemoryBufferSize)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var f in files)
            {
                try
                {
                    var snap = JsonSerializer.Deserialize<EconomySnapshot>(File.ReadAllText(f.FullName), JsonOptions);
                    if (snap != null)
                        AppendToBuffer(snap);
                }
                catch { /* skip corrupt */ }
            }
        }
        catch { /* dir issues */ }
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
            var dir = SnapshotsDir;
            var file = Path.Combine(dir, $"{snap.Timestamp}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(snap, JsonOptions));

            // Prune
            var all = new DirectoryInfo(dir).GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (all.Count > MaxSnapshotFiles)
            {
                foreach (var old in all.Skip(MaxSnapshotFiles))
                {
                    try { old.Delete(); } catch { }
                }
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
