using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>Applies approved BalanceSuggestions to a types.xml file, preserving structure.</summary>
public class XmlApplyService
{
    public ApplyResult Apply(IEnumerable<BalanceSuggestion> approved, string typesXmlPath, bool backup)
    {
        var result = new ApplyResult();

        if (!File.Exists(typesXmlPath))
        {
            result.Errors.Add($"types.xml not found: {typesXmlPath}");
            return result;
        }

        if (backup)
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var backupPath = $"{typesXmlPath}.backup.{stamp}";
                File.Copy(typesXmlPath, backupPath, overwrite: false);
                result.BackupPath = backupPath;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Backup failed: " + ex.Message);
                return result;
            }
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(typesXmlPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            result.Errors.Add("XML parse failed: " + ex.Message);
            return result;
        }

        var root = doc.Root;
        if (root == null)
        {
            result.Errors.Add("XML has no root element.");
            return result;
        }

        // Index <type name="..."> elements once for O(1) lookup.
        var index = root.Elements("type")
            .Where(e => e.Attribute("name") != null)
            .GroupBy(e => e.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var sug in approved)
        {
            if (sug == null || !sug.IsApproved || sug.Changes.Count == 0) continue;
            if (!index.TryGetValue(sug.ClassName, out var typeEl))
            {
                result.NotFound++;
                continue;
            }

            try
            {
                foreach (var kv in sug.Changes)
                    SetChildIntValue(typeEl, kv.Key, kv.Value.NewValue);
                result.Applied++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{sug.ClassName}: {ex.Message}");
            }
        }

        try
        {
            doc.Save(typesXmlPath, SaveOptions.DisableFormatting);
        }
        catch (Exception ex)
        {
            result.Errors.Add("XML save failed: " + ex.Message);
        }

        return result;
    }

    private static void SetChildIntValue(XElement typeEl, string childName, int value)
    {
        var child = typeEl.Element(childName);
        if (child == null)
        {
            child = new XElement(childName);
            typeEl.Add(child);
        }
        child.SetValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
