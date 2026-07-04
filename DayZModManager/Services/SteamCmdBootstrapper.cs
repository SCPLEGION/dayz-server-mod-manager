using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DayZModManager.Services;

/// <summary>
/// Downloads and extracts Valve's official SteamCMD distribution so the setup wizard needs no
/// manual download first. Network/filesystem only - process execution (including the mandatory
/// first-run self-update) stays in <see cref="SteamCmdClient"/>.
/// </summary>
internal static class SteamCmdBootstrapper
{
    private const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private static readonly HttpClient Http = new();

    /// <summary>Downloads steamcmd.zip into <paramref name="targetDir"/>, extracts it, and returns the resulting steamcmd.exe path.</summary>
    public static async Task<string> DownloadAndExtractAsync(string targetDir, Action<string>? onLog = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var zipPath = Path.Combine(targetDir, "steamcmd.zip");

        onLog?.Invoke($"[setup] downloading {SteamCmdZipUrl} ...");
        using (var resp = await Http.GetAsync(SteamCmdZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        onLog?.Invoke("[setup] extracting steamcmd.zip ...");
        ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);

        try { File.Delete(zipPath); } catch { /* best-effort cleanup */ }

        var exePath = Path.Combine(targetDir, "steamcmd.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException("steamcmd.exe not found after extracting steamcmd.zip.", exePath);

        return exePath;
    }
}
