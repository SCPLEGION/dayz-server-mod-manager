using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DayZModManager.Services;

/// <summary>
/// A DayZ XML merge preset: tells the engine which files to harvest from each mod folder,
/// which root element to treat as the merge target, and which attribute (if any) defines
/// a child's identity for dedupe.
/// </summary>
internal sealed record XmlMergePreset(
    string Id,
    string DisplayName,
    /// <summary>Glob patterns matched against the *filename only*. Multiple OR'd together.</summary>
    IReadOnlyList<string> IncludePatterns,
    /// <summary>Optional glob patterns excluded from the include set (filename only).</summary>
    IReadOnlyList<string> ExcludePatterns,
    /// <summary>Root element local name expected inside each source file.</summary>
    string RootElementName,
    /// <summary>
    /// Optional attribute on each child of <see cref="RootElementName"/> used as the dedupe key.
    /// When null/empty, the serialized element string is used as the key.
    /// </summary>
    string? KeyAttribute,
    /// <summary>Default output filename (resolved against the exe dir if relative).</summary>
    string OutputFileName,
    /// <summary>Free-form description shown in the UI tooltip.</summary>
    string Description = ""
);

internal sealed record XmlMergeConflict(string Key, string ExistingSource, string NewSource);

internal sealed record XmlMergeStats(
    string PresetDisplayName,
    int ModDirsScanned,
    int FilesFound,
    int CandidateChildrenFound,
    int MergedChildren,
    int UniqueKeys,
    int ConflictCount,
    IReadOnlyList<XmlMergeConflict> Conflicts,
    IReadOnlyList<string> SkippedInvalidFiles,
    IReadOnlyList<string> FilesScanned
);

internal static class XmlMergeService
{
    public static XmlMergeStats Preview(string modsRoot, XmlMergePreset preset, TypesXmlMergeMode mode)
        => BuildMerged(modsRoot, preset, mode).Stats;

    public static XmlMergeStats Generate(
        string modsRoot,
        XmlMergePreset preset,
        TypesXmlMergeMode mode,
        string outFile)
    {
        if (string.IsNullOrWhiteSpace(modsRoot))
            throw new ArgumentException("modsRoot is empty.", nameof(modsRoot));
        if (!Directory.Exists(modsRoot))
            throw new DirectoryNotFoundException(modsRoot);
        if (string.IsNullOrWhiteSpace(outFile))
            throw new ArgumentException("outFile is empty.", nameof(outFile));

        var (merged, stats) = BuildMerged(modsRoot, preset, mode);

        var outDir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(preset.RootElementName, merged));
        doc.Save(outFile);
        return stats;
    }

    private static (List<XElement> Merged, XmlMergeStats Stats) BuildMerged(
        string modsRoot,
        XmlMergePreset preset,
        TypesXmlMergeMode mode)
    {
        if (string.IsNullOrWhiteSpace(modsRoot))
            throw new ArgumentException("modsRoot is empty.", nameof(modsRoot));
        if (!Directory.Exists(modsRoot))
            throw new DirectoryNotFoundException(modsRoot);

        var merged       = new List<XElement>();
        var conflicts    = new List<XmlMergeConflict>();
        var skipped      = new List<string>();
        var filesScanned = new List<string>();
        var byKey        = new Dictionary<string, (XElement Element, string SourceFile)>(StringComparer.Ordinal);

        string KeyOf(XElement e)
        {
            if (!string.IsNullOrWhiteSpace(preset.KeyAttribute))
            {
                var v = e.Attribute(preset.KeyAttribute!)?.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return e.ToString(SaveOptions.DisableFormatting);
        }

        var modDirs       = Directory.EnumerateDirectories(modsRoot).ToArray();
        var totalFiles    = 0;
        var totalChildren = 0;

        foreach (var modDir in modDirs)
        {
            var files = CollectMatchingFiles(modDir, preset);
            totalFiles += files.Count;
            filesScanned.AddRange(files);

            foreach (var file in files)
            {
                XDocument doc;
                try { doc = XDocument.Load(file, LoadOptions.None); }
                catch { skipped.Add(file); continue; }

                var root = FindRoot(doc, preset.RootElementName);
                if (root == null) continue;

                foreach (var child in root.Elements())
                {
                    totalChildren++;
                    var key = KeyOf(child);

                    if (byKey.TryGetValue(key, out var existing))
                    {
                        conflicts.Add(new XmlMergeConflict(key, existing.SourceFile, file));
                        switch (mode)
                        {
                            case TypesXmlMergeMode.Append:
                                merged.Add(new XElement(child));
                                break;
                            case TypesXmlMergeMode.DedupeFirstByKey:
                                break; // keep existing
                            case TypesXmlMergeMode.DedupeLastByKey:
                                byKey[key] = (new XElement(child), file);
                                break;
                        }
                    }
                    else
                    {
                        byKey[key] = (new XElement(child), file);
                        if (mode == TypesXmlMergeMode.Append)
                            merged.Add(new XElement(child));
                    }
                }
            }
        }

        if (mode != TypesXmlMergeMode.Append)
            merged.AddRange(byKey.Values.Select(v => v.Element));

        var stats = new XmlMergeStats(
            PresetDisplayName: preset.DisplayName,
            ModDirsScanned: modDirs.Length,
            FilesFound: totalFiles,
            CandidateChildrenFound: totalChildren,
            MergedChildren: merged.Count,
            UniqueKeys: byKey.Count,
            ConflictCount: conflicts.Count,
            Conflicts: conflicts,
            SkippedInvalidFiles: skipped,
            FilesScanned: filesScanned
        );
        return (merged, stats);
    }

    /// <summary>
    /// Recursively enumerate files under <paramref name="modDir"/> whose filename matches any
    /// include glob and matches no exclude glob. Case-insensitive.
    /// </summary>
    private static List<string> CollectMatchingFiles(string modDir, XmlMergePreset preset)
    {
        var includes = preset.IncludePatterns ?? Array.Empty<string>();
        var excludes = preset.ExcludePatterns ?? Array.Empty<string>();
        if (includes.Count == 0) return new List<string>();

        // Build one big enumeration; we hit the disk once and filter in-memory.
        // We only look at XML files to avoid scanning unrelated trees too aggressively, but
        // patterns are free-form — if a pattern doesn't end in .xml we still keep it.
        var allXml = Directory.EnumerateFiles(modDir, "*.xml", SearchOption.AllDirectories);

        var includeRx = includes.Select(GlobToRegex).ToArray();
        var excludeRx = excludes.Select(GlobToRegex).ToArray();

        return allXml
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                var ok = includeRx.Any(rx => rx.IsMatch(name));
                if (!ok) return false;
                if (excludeRx.Any(rx => rx.IsMatch(name))) return false;
                return true;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Regex GlobToRegex(string pattern)
    {
        var rx = "^" + Regex.Escape(pattern)
                          .Replace("\\*", ".*")
                          .Replace("\\?", ".") + "$";
        return new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static XElement? FindRoot(XDocument doc, string rootName)
    {
        var root = doc.Root;
        if (root != null && root.Name.LocalName.Equals(rootName, StringComparison.OrdinalIgnoreCase))
            return root;
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(rootName, StringComparison.OrdinalIgnoreCase));
    }
}
