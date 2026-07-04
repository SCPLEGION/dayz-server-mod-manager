using System.IO;

namespace DayZModManager;

/// <summary>
/// Canonical path layout used by the manager:
///   <c>parent1/parent2/DayZModManager.exe</c>  - the EXE lives in <see cref="ExeDir"/> ("parent2")
///   <c>parent1/parent2/mods.txt</c>            - mods.txt sits next to the exe
///   <c>parent1/&lt;mod folders&gt;/</c>        - mod directories live in <see cref="DefaultModsRoot"/> ("parent1")
/// </summary>
internal static class AppPaths
{
    /// <summary>Directory the exe is running from (a.k.a. "parent2").</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>One level above the exe directory (a.k.a. "parent1") — default mods root.</summary>
    public static string DefaultModsRoot =>
        Path.GetFullPath(Path.Combine(ExeDir, ".."));

    /// <summary>The mods.txt file, kept next to the exe in <see cref="ExeDir"/>.</summary>
    public static string ModsTxtPath =>
        Path.Combine(ExeDir, "mods.txt");

    /// <summary>Default DayZ server profile directory: <c>parent2/ServerProfile</c>.</summary>
    public static string DefaultServerProfileDir =>
        Path.Combine(ExeDir, "ServerProfile");

    /// <summary>Default RPT log directory — DayZ writes RPT files into the profile dir.</summary>
    public static string DefaultRptDir => DefaultServerProfileDir;

    /// <summary>Default SteamCMD mod cache directory (persistent <c>+force_install_dir</c>).</summary>
    public static string DefaultModCacheDir =>
        Path.Combine(ExeDir, "steamcmd-cache");

    /// <summary>
    /// Default extraction target for a bootstrapped steamcmd.exe. Intentionally the same
    /// directory as <see cref="DefaultModCacheDir"/> - elsewhere in the app ModCacheDir is always
    /// derived as "the folder containing steamcmd.exe", so these must never diverge.
    /// </summary>
    public static string DefaultSteamCmdDir => DefaultModCacheDir;

    /// <summary>Default install target when the setup wizard downloads a new DayZ server: <c>parent2/DayZServer</c>.</summary>
    public static string DefaultServerInstallDir =>
        Path.Combine(ExeDir, "DayZServer");

    /// <summary>Resolve a possibly-relative output file path against <see cref="ExeDir"/>.</summary>
    public static string ResolveOutputPath(string maybeRelative)
    {
        if (string.IsNullOrWhiteSpace(maybeRelative))
            return Path.Combine(ExeDir, "out.xml");
        return Path.IsPathRooted(maybeRelative)
            ? maybeRelative
            : Path.Combine(ExeDir, maybeRelative);
    }
}
