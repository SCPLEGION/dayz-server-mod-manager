using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DayZModManager.Services;

internal sealed record DeployedMod(ulong Id, string AtName, string SourcePath);

internal sealed class SteamCmdClient
{
    /// <summary>
    /// Steam App ID for the DayZ Dedicated Server itself - distinct from
    /// <see cref="ServerConfig.WorkshopAppId"/> (221100, the game's appid used for workshop
    /// mod downloads). Don't conflate the two.
    /// </summary>
    public const uint DayZDedicatedServerAppId = 223350;

    private readonly string _steamCmdPath;

    public SteamCmdClient(string steamCmdPath)
    {
        _steamCmdPath = steamCmdPath;
    }

    public bool Probe()
    {
        if (string.IsNullOrWhiteSpace(_steamCmdPath)) return false;
        return File.Exists(_steamCmdPath);
    }

    /// <summary>
    /// Best-effort probe for whether SteamCMD has a cached credential for <paramref name="login"/>.
    /// Returns false on any uncertainty (caller defaults to interactive on first run).
    /// </summary>
    public bool HasCachedCredential(string? login)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(_steamCmdPath))
            return false;
        try
        {
            var dir = Path.GetDirectoryName(_steamCmdPath);
            if (string.IsNullOrEmpty(dir)) return false;
            var vdf = Path.Combine(dir, "config", "config.vdf");
            if (!File.Exists(vdf)) return false;
            var text = File.ReadAllText(vdf);
            return text.IndexOf(login, StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("ConnectCache", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs SteamCMD with stdout redirected. Lines stream out as they arrive. Caller awaits the final
    /// task (yielded via the sentinel pattern) by reading until the enumerable completes; the exit
    /// code is appended as the final yielded line with the prefix "__EXIT__:".
    /// </summary>
    public IAsyncEnumerable<string> DownloadModsStreamedAsync(
        IEnumerable<ulong> ids,
        uint appId,
        string cacheDir,
        string login,
        bool validate,
        CancellationToken ct = default)
        => RunStreamedAsync(BuildArgs(ids, appId, cacheDir, login, validate), ct);

    /// <summary>
    /// Runs SteamCMD in a visible console window with NO stdin/stdout redirection. The user types
    /// password and Steam Guard code in the SteamCMD console themselves; this app never sees them.
    /// Returns the process exit code.
    /// </summary>
    public Task<int> DownloadModsInteractiveAsync(
        IEnumerable<ulong> ids,
        uint appId,
        string cacheDir,
        string login,
        bool validate,
        CancellationToken ct = default)
        => RunInteractiveAsync(BuildArgs(ids, appId, cacheDir, login, validate), ct);

    /// <summary>
    /// Installs/updates a whole Steam app (e.g. the DayZ Dedicated Server itself, see
    /// <see cref="DayZDedicatedServerAppId"/>) via <c>+app_update</c>, streamed the same way
    /// <see cref="DownloadModsStreamedAsync"/> streams mod downloads.
    /// </summary>
    public IAsyncEnumerable<string> DownloadAppStreamedAsync(
        uint appId, string installDir, string login, bool validate, CancellationToken ct = default)
        => RunStreamedAsync(BuildAppUpdateArgs(appId, installDir, login, validate), ct);

    /// <summary>
    /// Same as <see cref="DownloadModsInteractiveAsync"/> but installs/updates the app itself
    /// (e.g. <see cref="DayZDedicatedServerAppId"/>) via <c>+app_update</c> - a real visible
    /// console window; this app process never sees the password.
    /// </summary>
    public Task<int> DownloadAppInteractiveAsync(
        uint appId, string installDir, string login, bool validate, CancellationToken ct = default)
        => RunInteractiveAsync(BuildAppUpdateArgs(appId, installDir, login, validate), ct);

    /// <summary>
    /// A freshly-extracted steamcmd.exe always self-updates on its first run before anything
    /// else will work. Run this once right after bootstrapping, before using it for real work.
    /// </summary>
    public IAsyncEnumerable<string> RunSelfUpdateAsync(CancellationToken ct = default)
        => RunStreamedAsync("+quit", ct);

    private async IAsyncEnumerable<string> RunStreamedAsync(string args, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _steamCmdPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_steamCmdPath) ?? string.Empty,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lines = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Writer.TryWrite(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lines.Writer.TryWrite(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var _ = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        var waiter = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); } catch { }
            lines.Writer.TryComplete();
        }, ct);

        await foreach (var line in lines.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return line;
        }

        await waiter.ConfigureAwait(false);
        yield return $"__EXIT__:{proc.ExitCode}";
    }

    private async Task<int> RunInteractiveAsync(string args, CancellationToken ct = default)
    {
        // UseShellExecute=true gives the user a real console window they can type into. This
        // must stay a mechanical copy of that invariant - never redirect stdio here, that's what
        // guarantees this process never sees the Steam password.
        var psi = new ProcessStartInfo
        {
            FileName = _steamCmdPath,
            Arguments = args,
            UseShellExecute = true,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(_steamCmdPath) ?? string.Empty,
        };

        using var proc = Process.Start(psi);
        if (proc == null) return -1;

        using var _ = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return -1; }

        return proc.ExitCode;
    }

    private static string BuildArgs(IEnumerable<ulong> ids, uint appId, string cacheDir, string login, bool validate)
    {
        var sb = new StringBuilder();
        sb.Append("+force_install_dir ").Append(Quote(cacheDir));
        sb.Append(" +login ").Append(string.IsNullOrWhiteSpace(login) ? "anonymous" : login);
        foreach (var id in ids.Distinct())
        {
            sb.Append(" +workshop_download_item ").Append(appId).Append(' ').Append(id);
            if (validate) sb.Append(" validate");
        }
        sb.Append(" +quit");
        return sb.ToString();
    }

    private static string BuildAppUpdateArgs(uint appId, string installDir, string login, bool validate)
    {
        var sb = new StringBuilder();
        sb.Append("+force_install_dir ").Append(Quote(installDir));
        sb.Append(" +login ").Append(string.IsNullOrWhiteSpace(login) ? "anonymous" : login);
        sb.Append(" +app_update ").Append(appId);
        if (validate) sb.Append(" validate");
        sb.Append(" +quit");
        return sb.ToString();
    }

    private static string Quote(string s) =>
        string.IsNullOrEmpty(s) ? "\"\"" : (s.Contains(' ') ? $"\"{s}\"" : s);

    /// <summary>
    /// Links/copies each cached workshop folder to <paramref name="serverRootDir"/> as <c>@&lt;modname&gt;</c>.
    /// Cleans up stale links pointing into <paramref name="cacheDir"/> for IDs no longer in <paramref name="ids"/>.
    /// Hand-placed directories that aren't reparse points into our cache are left alone.
    /// </summary>
    public static IReadOnlyList<DeployedMod> DeployMods(
        string cacheDir,
        uint appId,
        IEnumerable<ulong> ids,
        string serverRootDir,
        ModDeployMode mode)
    {
        if (string.IsNullOrWhiteSpace(serverRootDir))
            throw new ArgumentException("Server root directory must be set.", nameof(serverRootDir));
        Directory.CreateDirectory(serverRootDir);

        var idList = ids.Distinct().ToList();
        var workshopRoot = Path.Combine(cacheDir, "steamapps", "workshop", "content", appId.ToString());

        // Build the deploy list (id -> source path -> @modname).
        var deployed = new List<DeployedMod>();
        var wantedAtNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in idList)
        {
            var source = Path.Combine(workshopRoot, id.ToString());
            if (!Directory.Exists(source))
                continue;

            var modName = ReadMetaName(source) ?? id.ToString();
            var atName = "@" + SanitizeAtName(modName);
            var target = Path.Combine(serverRootDir, atName);

            DeployOne(source, target, mode);
            deployed.Add(new DeployedMod(id, atName, source));
            wantedAtNames.Add(atName);
        }

        // Stale cleanup: only remove @* entries that are reparse points pointing into our cache
        // and aren't in the wanted set. Hand-placed directories or copies are never touched here.
        foreach (var entry in Directory.EnumerateDirectories(serverRootDir, "@*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var name = Path.GetFileName(entry);
                if (wantedAtNames.Contains(name)) continue;

                var info = new DirectoryInfo(entry);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                    continue;

                var linkTarget = info.LinkTarget;
                if (string.IsNullOrEmpty(linkTarget))
                    continue;

                var fullLinkTarget = Path.GetFullPath(linkTarget);
                var fullCacheRoot = Path.GetFullPath(workshopRoot);
                if (fullLinkTarget.StartsWith(fullCacheRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(entry, recursive: false);
                }
            }
            catch
            {
                // Best-effort cleanup; ignore individual failures.
            }
        }

        return deployed;
    }

    private static void DeployOne(string source, string target, ModDeployMode mode)
    {
        // If a reparse point already exists, remove it first so we can re-create.
        if (Directory.Exists(target))
        {
            var info = new DirectoryInfo(target);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Already a link; if it points where we want, no-op.
                if (string.Equals(info.LinkTarget, source, StringComparison.OrdinalIgnoreCase))
                    return;
                Directory.Delete(target, recursive: false);
            }
            else
            {
                // Real directory (copy or hand-placed). Re-deploy by removing and recreating
                // only when we're in Copy mode; otherwise leave it alone to avoid clobbering user data.
                if (mode != ModDeployMode.Copy) return;
                Directory.Delete(target, recursive: true);
            }
        }

        switch (mode)
        {
            case ModDeployMode.Junction:
                CreateJunction(target, source);
                break;
            case ModDeployMode.Symlink:
                Directory.CreateSymbolicLink(target, source);
                break;
            case ModDeployMode.Copy:
            default:
                CopyDirectory(source, target);
                break;
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // mklink /J creates a directory junction; no admin / Developer Mode required on NTFS.
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc == null) throw new IOException("Failed to start cmd.exe for junction creation.");
        if (!proc.WaitForExit(10_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new IOException($"mklink /J timed out creating junction \"{link}\".");
        }
        if (proc.ExitCode != 0)
            throw new IOException($"mklink /J failed (exit {proc.ExitCode}): {proc.StandardError.ReadToEnd()}");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target), overwrite: true);
        }
    }

    private static string? ReadMetaName(string modFolder)
    {
        try
        {
            var meta = Path.Combine(modFolder, "meta.cpp");
            if (!File.Exists(meta)) return null;
            foreach (var line in File.ReadLines(meta))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                var value = trimmed.Substring(eq + 1).Trim().TrimEnd(';').Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private static string SanitizeAtName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' || ch == '@')
                sb.Append('_');
            else
                sb.Append(ch);
        }
        return sb.ToString().Trim('_');
    }
}
