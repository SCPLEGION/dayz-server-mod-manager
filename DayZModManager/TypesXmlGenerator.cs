using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DayZModManager;

internal static class TypesXmlGenerator
{
    public sealed record MergeConflict(string Key, string? ExistingSourceFile, string NewSourceFile);
    public sealed record MergeStats(
        int ModDirsScanned,
        int TypesXmlFilesFound,
        int CandidateTypesElementsFound,
        int MergedTypeChildren,
        int UniqueTypeKeys,
        int ConflictCount,
        List<MergeConflict> Conflicts
    );

    public static void Generate(string modsRoot, string outFile)
    {
        // Default keeps behavior similar to the old generator: dedupe to one entry per "type key".
        Generate(modsRoot, outFile, TypesXmlMergeMode.DedupeFirstByKey);
    }

    public static void Generate(string modsRoot, string outFile, TypesXmlMergeMode mergeMode)
    {
        var result = BuildMergedTypes(modsRoot, mergeMode);
        var mergedTypes = result.MergedTypes;

        if (string.IsNullOrWhiteSpace(modsRoot))
            throw new ArgumentException("modsRoot is empty.", nameof(modsRoot));

        if (!Directory.Exists(modsRoot))
            throw new DirectoryNotFoundException(modsRoot);

        var outDir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        // Keep it simple: output as UTF-8 without XML namespaces.
        var outDoc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("types", mergedTypes));

        outDoc.Save(outFile);
    }

    public static MergeStats Preview(string modsRoot, TypesXmlMergeMode mergeMode)
    {
        var result = BuildMergedTypes(modsRoot, mergeMode);
        return result.Stats;
    }

    private sealed record BuildMergedTypesResult(List<XElement> MergedTypes, MergeStats Stats);

    private static BuildMergedTypesResult BuildMergedTypes(string modsRoot, TypesXmlMergeMode mergeMode)
    {
        var mergedTypes = new List<XElement>();
        var statsConflicts = new List<MergeConflict>();

        // Key dedupe strategy:
        // - If <type name="..."> exists, key=name.
        // - Otherwise fall back to stable serialized element (old behavior-ish).
        string GetTypeKey(XElement child)
        {
            var nameAttr = child.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(nameAttr))
                return nameAttr.Trim();

            // Fallback when attributes aren't available.
            return child.ToString(SaveOptions.DisableFormatting);
        }

        var mergedByKey = new Dictionary<string, (XElement Element, string SourceFile)>(StringComparer.Ordinal);

        var modDirs = Directory.EnumerateDirectories(modsRoot).ToArray();
        var typesXmlFiles = 0;
        var candidateTypeChildren = 0;

        foreach (var modDir in modDirs)
        {
            // files we merge:
            //  - types.xml
            //  - anything matching *_types.xml (e.g. Morty_types.xml)
            var candidateFiles = new List<string>();

            var typesPath = Path.Combine(modDir, "types.xml");
            if (File.Exists(typesPath))
                candidateFiles.Add(typesPath);

            // Some mods store their types in nested folders (e.g. "XML & More").
            // We search recursively for *_types.xml (like Morty_types.xml).
            candidateFiles.AddRange(Directory.EnumerateFiles(modDir, "*_types.xml", SearchOption.AllDirectories));
            candidateFiles = candidateFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();

            typesXmlFiles += candidateFiles.Count;

            foreach (var file in candidateFiles)
            {
                try
                {
                    var doc = XDocument.Load(file, LoadOptions.None);
                    var typesElement = FindTypesElement(doc);
                    if (typesElement == null)
                        continue;

                    foreach (var child in typesElement.Elements())
                    {
                        candidateTypeChildren++;
                        var key = GetTypeKey(child);

                        if (mergedByKey.TryGetValue(key, out var existing))
                        {
                            statsConflicts.Add(new MergeConflict(key, existing.SourceFile, file));

                            switch (mergeMode)
                            {
                                case TypesXmlMergeMode.Append:
                                    // Append keeps everything, but we still track conflicts.
                                    mergedTypes.Add(new XElement(child));
                                    break;
                                case TypesXmlMergeMode.DedupeFirstByKey:
                                    // keep existing
                                    break;
                                case TypesXmlMergeMode.DedupeLastByKey:
                                    mergedByKey[key] = (new XElement(child), file);
                                    break;
                            }
                        }
                        else
                        {
                            mergedByKey[key] = (new XElement(child), file);
                            if (mergeMode == TypesXmlMergeMode.Append)
                                mergedTypes.Add(new XElement(child));
                        }
                    }
                }
                catch
                {
                    // Skip invalid XML files instead of failing the whole generation.
                }
            }
        }

        if (mergeMode != TypesXmlMergeMode.Append)
        {
            mergedTypes.AddRange(mergedByKey.Values.Select(v => v.Element));
        }

        var mergedUniqueKeys = mergedByKey.Count;
        var stats = new MergeStats(
            ModDirsScanned: modDirs.Length,
            TypesXmlFilesFound: typesXmlFiles,
            CandidateTypesElementsFound: candidateTypeChildren,
            MergedTypeChildren: mergedTypes.Count,
            UniqueTypeKeys: mergedUniqueKeys,
            ConflictCount: statsConflicts.Count,
            Conflicts: statsConflicts
        );

        return new BuildMergedTypesResult(mergedTypes, stats);
    }

    private static XElement? FindTypesElement(XDocument doc)
    {
        // types.xml usually has <types> as root, but tolerate nested occurrences.
        var root = doc.Root;
        if (root != null && root.Name.LocalName.Equals("types", StringComparison.OrdinalIgnoreCase))
            return root;

        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("types", StringComparison.OrdinalIgnoreCase));
    }
}
