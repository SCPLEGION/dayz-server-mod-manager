using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DayZModManager.Services;

/// <summary>
/// Keeps BattlEye's own BEServer_x64.cfg in sync with the manager's configured RCON port/password
/// so the two never drift. BE reads this file from the folder passed via the server's -BEpath
/// launch argument - this app always launches with "-BEpath=battleye" when BattlEye is enabled,
/// so that resolves to &lt;serverRootDir&gt;/battleye/BEServer_x64.cfg. Best-effort only: a write
/// failure here must never block the server from starting.
/// </summary>
internal static class BattlEyeServerCfgWriter
{
    private const string FileName = "BEServer_x64.cfg";

    public static void EnsureConfigured(string serverRootDir, int port, string password)
    {
        var dir = Path.Combine(serverRootDir, "battleye");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);

        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        SetOrAddKey(lines, "RConPassword", password);
        SetOrAddKey(lines, "RConPort", port.ToString());

        File.WriteAllLines(path, lines);
    }

    private static void SetOrAddKey(List<string> lines, string key, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key, StringComparison.Ordinal) &&
                (trimmed.Length == key.Length || char.IsWhiteSpace(trimmed[key.Length])))
            {
                lines[i] = $"{key} {value}";
                return;
            }
        }
        lines.Add($"{key} {value}");
    }
}
