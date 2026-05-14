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
