using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DayZModManager;

internal static class TypesXmlGenerator
{
    public static void Generate(string modsRoot, string outFile)
    {
        if (string.IsNullOrWhiteSpace(modsRoot))
            throw new ArgumentException("modsRoot is empty.", nameof(modsRoot));

        if (!Directory.Exists(modsRoot))
            throw new DirectoryNotFoundException(modsRoot);

        var mergedTypes = new List<XElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modDir in Directory.EnumerateDirectories(modsRoot))
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
                        var key = child.ToString(SaveOptions.DisableFormatting);
                        if (seen.Add(key))
                            mergedTypes.Add(new XElement(child));
                    }
                }
                catch
                {
                    // Skip invalid XML files instead of failing the whole generation.
                }
            }
        }

        var outDir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        // Keep it simple: output as UTF-8 without XML namespaces.
        var outDoc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("types", mergedTypes));

        outDoc.Save(outFile);
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
