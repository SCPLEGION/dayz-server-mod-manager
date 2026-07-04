using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DayZModManager.Services;

/// <summary>
/// Minimal client for BattlEye's RCon protocol (BERCon) - a UDP protocol, not related to
/// Source-engine RCON. Handles login, command/response correlation (including multi-packet
/// responses), unsolicited server messages (with required acknowledgement), and the ~40s
/// keep-alive BE requires to avoid being dropped as an idle client.
/// Protocol reference: https://www.battleye.com/downloads/BERConProtocol.txt
/// </summary>
internal sealed class BattlEyeRconClient : IDisposable
{
    private const byte PacketMarker = 0xFF;
    private const byte TypeLogin = 0x00;
    private const byte TypeCommand = 0x01;
    private const byte TypeServerMessage = 0x02;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Timer? _keepAliveTimer;
    private readonly object _lock = new();
    private readonly Dictionary<byte, TaskCompletionSource<string>> _pending = new();
    private readonly Dictionary<byte, StringBuilder> _multiPacketBuffers = new();
    private TaskCompletionSource<bool>? _loginTcs;
    private byte _seq;
    private bool _disposed;

    public bool IsConnected { get; private set; }

    /// <summary>Fires for unsolicited server console messages (chat, admin log lines).</summary>
    public event Action<string>? MessageReceived;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        Disconnect();

        try
        {
            _udp = new UdpClient();
            _udp.Connect(host, port);
        }
        catch
        {
            Disconnect();
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(_cts.Token);

        _loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await SendRawAsync(BuildPacket(TypeLogin, Encoding.UTF8.GetBytes(password))).ConfigureAwait(false);
        }
        catch
        {
            Disconnect();
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        using var reg = timeoutCts.Token.Register(() => _loginTcs.TrySetResult(false));

        IsConnected = await _loginTcs.Task.ConfigureAwait(false);

        if (IsConnected)
        {
            // BE drops idle clients after ~45s with no command packets; keep well under that.
            _keepAliveTimer = new Timer(_ => _ = SendKeepAliveAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        else
        {
            Disconnect();
        }

        return IsConnected;
    }

    private async Task SendKeepAliveAsync()
    {
        try { await SendCommandAsync(string.Empty, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* best-effort; a failed keep-alive just means the next one tries again */ }
    }

    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var udp = _udp ?? throw new InvalidOperationException("Not connected.");

        byte seq;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            seq = _seq++;
            _pending[seq] = tcs;
        }

        try
        {
            var cmdBytes = Encoding.UTF8.GetBytes(command);
            var typeData = new byte[1 + cmdBytes.Length];
            typeData[0] = seq;
            Array.Copy(cmdBytes, 0, typeData, 1, cmdBytes.Length);
            await SendRawAsync(BuildPacket(TypeCommand, typeData)).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(seq);
                _multiPacketBuffers.Remove(seq);
            }
        }
    }

    // ---- convenience commands (BE server console command syntax) ----

    public Task<string> GetPlayersAsync(TimeSpan timeout) => SendCommandAsync("players", timeout);
    public Task<string> GetBansAsync(TimeSpan timeout) => SendCommandAsync("bans", timeout);

    public Task<string> KickAsync(int playerId, string? reason, TimeSpan timeout) =>
        SendCommandAsync(string.IsNullOrWhiteSpace(reason) ? $"kick {playerId}" : $"kick {playerId} {reason}", timeout);

    public Task<string> BanAsync(int playerId, int durationMinutes, string? reason, TimeSpan timeout) =>
        SendCommandAsync($"ban {playerId} {durationMinutes} {reason ?? string.Empty}".TrimEnd(), timeout);

    public Task<string> RemoveBanAsync(int banId, TimeSpan timeout) =>
        SendCommandAsync($"removeban {banId}", timeout);

    public Task<string> BroadcastAsync(string message, TimeSpan timeout) =>
        SendCommandAsync($"say -1 {message}", timeout);

    // ---- receive/dispatch ----

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                HandlePacket(result.Buffer);
            }
        }
        finally
        {
            var wasConnected = IsConnected;
            IsConnected = false;
            if (wasConnected) Disconnected?.Invoke();
        }
    }

    private void HandlePacket(byte[] data)
    {
        // "BE" + 4-byte CRC32 (unchecked on receive - trusting the OS/UDP checksum is enough
        // for a same-LAN admin link) + 0xFF marker + type + type-specific data.
        if (data.Length < 8 || data[0] != (byte)'B' || data[1] != (byte)'E' || data[6] != PacketMarker)
            return;

        var type = data[7];
        switch (type)
        {
            case TypeLogin:
                var ok = data.Length > 8 && data[8] == 0x01;
                _loginTcs?.TrySetResult(ok);
                break;

            case TypeCommand:
                HandleCommandResponse(data);
                break;

            case TypeServerMessage:
                HandleServerMessage(data);
                break;
        }
    }

    private void HandleCommandResponse(byte[] data)
    {
        if (data.Length < 9) return;
        var seq = data[8];
        var start = 9;

        // Multi-packet header: 0x00 <total packets> <this packet's 0-based index>.
        if (data.Length >= 12 && data[9] == 0x00)
        {
            var total = data[10];
            var index = data[11];
            start = 12;
            var chunk = data.Length > start ? Encoding.UTF8.GetString(data, start, data.Length - start) : string.Empty;

            lock (_lock)
            {
                if (!_multiPacketBuffers.TryGetValue(seq, out var sb))
                    _multiPacketBuffers[seq] = sb = new StringBuilder();
                sb.Append(chunk);

                if (index == total - 1 && _pending.TryGetValue(seq, out var tcs))
                {
                    tcs.TrySetResult(sb.ToString());
                    _multiPacketBuffers.Remove(seq);
                }
            }
            return;
        }

        var text = data.Length > start ? Encoding.UTF8.GetString(data, start, data.Length - start) : string.Empty;
        lock (_lock)
        {
            if (_pending.TryGetValue(seq, out var tcs)) tcs.TrySetResult(text);
        }
    }

    private void HandleServerMessage(byte[] data)
    {
        if (data.Length < 9) return;
        var seq = data[8];
        var msg = data.Length > 9 ? Encoding.UTF8.GetString(data, 9, data.Length - 9) : string.Empty;

        // BE requires the same packet type/sequence echoed back within its ack window, or it
        // drops this client from the authenticated list after a few retries.
        _ = SendRawAsync(BuildPacket(TypeServerMessage, new[] { seq }));
        MessageReceived?.Invoke(msg);
    }

    private Task SendRawAsync(byte[] packet)
    {
        var udp = _udp;
        return udp == null ? Task.CompletedTask : udp.SendAsync(packet, packet.Length);
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _keepAliveTimer?.Dispose(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }

        _cts = null;
        _keepAliveTimer = null;
        _udp = null;
        IsConnected = false;

        lock (_lock)
        {
            _pending.Clear();
            _multiPacketBuffers.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ---- packet framing ----

    private static byte[] BuildPacket(byte type, byte[] typeSpecific)
    {
        // payload = 0xFF + type + type-specific data; CRC32 covers exactly this payload.
        var payload = new byte[2 + typeSpecific.Length];
        payload[0] = PacketMarker;
        payload[1] = type;
        Array.Copy(typeSpecific, 0, payload, 2, typeSpecific.Length);

        var crc = Crc32(payload);
        var packet = new byte[6 + payload.Length];
        packet[0] = (byte)'B';
        packet[1] = (byte)'E';
        packet[2] = (byte)(crc & 0xFF);
        packet[3] = (byte)((crc >> 8) & 0xFF);
        packet[4] = (byte)((crc >> 16) & 0xFF);
        packet[5] = (byte)((crc >> 24) & 0xFF);
        Array.Copy(payload, 0, packet, 6, payload.Length);
        return packet;
    }

    private static uint[] BuildCrc32Table()
    {
        const uint poly = 0xEDB88320;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
