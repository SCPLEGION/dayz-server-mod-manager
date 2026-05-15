using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DayZModManager.Services;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed
}

internal sealed class ServerProcessController : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _intentionalStop;
    private int _retryCount;
    private CancellationTokenSource? _autoRestartCts;
    private ServerLaunchSpec? _lastSpec;
    private DateTime? _startedAt;
    private bool _disposed;

    // Auto-restart settings (set from ServerConfig).
    public bool AutoRestartOnCrash { get; set; }
    public int AutoRestartBackoffSeconds { get; set; } = 10;
    public int AutoRestartMaxRetries { get; set; } = 3;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public int? Pid => _process?.Id;
    public DateTime? StartedAt => _startedAt;
    public int? LastExitCode { get; private set; }

    public event Action<ServerState>? StateChanged;
    public event Action<int, bool>? Exited; // (exitCode, crashed)

    public async Task StartAsync(ServerLaunchSpec spec, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ServerState.Running || State == ServerState.Starting)
                return;

            _lastSpec = spec;
            _intentionalStop = false;
            SetState(ServerState.Starting);

            var psi = BuildStartInfo(spec);
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;

            if (!_process.Start())
            {
                SetState(ServerState.Stopped);
                ServerHistoryLogger.Append("start-failed", spec.Mode.ToString(), detail: "Process.Start returned false");
                return;
            }

            _startedAt = DateTime.UtcNow;
            ServerHistoryLogger.Append("start", spec.Mode.ToString(), pid: _process.Id);
            SetState(ServerState.Running);

            // Reset retry counter after a successful long-lived launch.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                    if (State == ServerState.Running) _retryCount = 0;
                }
                catch { }
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(TimeSpan _graceful)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _autoRestartCts?.Cancel();
            _intentionalStop = true;

            if (_process == null || _process.HasExited)
            {
                SetState(ServerState.Stopped);
                return;
            }

            SetState(ServerState.Stopping);
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                ServerHistoryLogger.Append("stop-failed", _lastSpec?.Mode.ToString() ?? "?", detail: ex.Message);
            }
            ServerHistoryLogger.Append("stop", _lastSpec?.Mode.ToString() ?? "?", pid: _process?.Id);
            SetState(ServerState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(ServerLaunchSpec spec, CancellationToken ct = default)
    {
        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        await StartAsync(spec, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Disconnect this controller from any running process WITHOUT killing it. Used at window-close:
    /// the user closes the manager often, but Stop is intentional, so we don't tear down the server.
    /// </summary>
    public void Detach()
    {
        try
        {
            if (_process != null)
            {
                _process.EnableRaisingEvents = false;
                _process.Exited -= OnProcessExited;
            }
        }
        catch { }
        _process = null;
        _autoRestartCts?.Cancel();
        SetState(ServerState.Stopped);
    }

    /// <summary>Attach to an externally running server (best-effort).</summary>
    public void Attach(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            _process = p;
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            _startedAt = DateTime.UtcNow; // can't recover real uptime
            SetState(ServerState.Running);
        }
        catch
        {
            // Process likely exited between scan and attach; leave Stopped.
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        try
        {
            var exitCode = -1;
            try { exitCode = _process?.ExitCode ?? -1; } catch { }
            LastExitCode = exitCode;

            var crashed = !_intentionalStop && exitCode != 0;
            ServerHistoryLogger.Append(
                crashed ? "crash" : "exit",
                _lastSpec?.Mode.ToString() ?? "?",
                pid: _process?.Id,
                exitCode: exitCode);

            Exited?.Invoke(exitCode, crashed);

            if (crashed && AutoRestartOnCrash && _lastSpec != null)
            {
                if (_retryCount >= AutoRestartMaxRetries)
                {
                    SetState(ServerState.Crashed);
                    ServerHistoryLogger.Append("auto-restart-cap-hit", _lastSpec.Mode.ToString(),
                        detail: $"retries={_retryCount}");
                    return;
                }

                _retryCount++;
                _autoRestartCts?.Dispose();
                _autoRestartCts = new CancellationTokenSource();
                var token = _autoRestartCts.Token;
                var backoff = TimeSpan.FromSeconds(AutoRestartBackoffSeconds);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(backoff, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;
                        ServerHistoryLogger.Append("auto-restart", _lastSpec.Mode.ToString(),
                            detail: $"retry={_retryCount}/{AutoRestartMaxRetries}");
                        await StartAsync(_lastSpec, token).ConfigureAwait(false);
                    }
                    catch { /* swallow; state stays Crashed if we fail */ }
                });
            }
            else
            {
                SetState(crashed ? ServerState.Crashed : ServerState.Stopped);
            }
        }
        catch { /* never throw on event */ }
    }

    private void SetState(ServerState s)
    {
        if (State == s) return;
        State = s;
        try { StateChanged?.Invoke(s); } catch { }
    }

    private static ProcessStartInfo BuildStartInfo(ServerLaunchSpec spec)
    {
        return spec.Mode switch
        {
            ServerLaunchMode.Ps1 => BuildPs1StartInfo(spec),
            ServerLaunchMode.DirectExe => BuildDirectStartInfo(spec),
            _ => throw new InvalidOperationException($"Unknown launch mode: {spec.Mode}")
        };
    }

    private static ProcessStartInfo BuildPs1StartInfo(ServerLaunchSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Ps1Path) || !File.Exists(spec.Ps1Path))
            throw new FileNotFoundException("servermanager.ps1 not found.", spec.Ps1Path ?? "(unset)");

        var pwsh = ResolvePowerShell();
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{spec.Ps1Path}\"",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(spec.Ps1Path) ?? string.Empty,
        };
        return psi;
    }

    private static ProcessStartInfo BuildDirectStartInfo(ServerLaunchSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.ExePath) || !File.Exists(spec.ExePath))
            throw new FileNotFoundException("DayZ server exe not found.", spec.ExePath ?? "(unset)");

        var args = new StringBuilder();
        if (spec.Port is int port)
            args.Append("-port=").Append(port).Append(' ');

        if (!string.IsNullOrWhiteSpace(spec.ProfileDir))
            args.Append("\"-profiles=").Append(spec.ProfileDir).Append("\" ");

        if (spec.DeployedAtNames.Count > 0)
            args.Append("\"-mod=").Append(string.Join(";", spec.DeployedAtNames)).Append("\" ");

        if (spec.BattlEye)
            args.Append("-BEpath=battleye ");
        else
            args.Append("-noBEinstaller ");

        if (!string.IsNullOrWhiteSpace(spec.ExtraArgs))
            args.Append(spec.ExtraArgs).Append(' ');

        var workingDir = spec.ServerRootDir
            ?? Path.GetDirectoryName(spec.ExePath)
            ?? string.Empty;

        return new ProcessStartInfo
        {
            FileName = spec.ExePath,
            Arguments = args.ToString().TrimEnd(),
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = workingDir,
        };
    }

    private static string _resolvedPwsh = string.Empty;
    private static string ResolvePowerShell()
    {
        if (!string.IsNullOrEmpty(_resolvedPwsh)) return _resolvedPwsh;
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            try
            {
                var psi = new ProcessStartInfo("where", candidate)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0)
                {
                    var first = stdout.Split('\n').FirstOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(first))
                    {
                        _resolvedPwsh = first;
                        return first;
                    }
                }
            }
            catch { /* try next */ }
        }
        // Last-ditch fallback (let CreateProcess error out helpfully if neither is on PATH).
        _resolvedPwsh = "powershell.exe";
        return _resolvedPwsh;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _autoRestartCts?.Dispose();
        _gate.Dispose();
    }
}
