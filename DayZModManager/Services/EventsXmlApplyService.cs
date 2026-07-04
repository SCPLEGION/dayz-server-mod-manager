using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// Applies approved BalanceSuggestions targeting events.xml (zombie/animal spawn nominal/min/
/// max/lifetime). Structurally different from types.xml: nominal/min/max/lifetime live on the
/// &lt;event name="..."&gt; element itself, not per child class, so suggestions are looked up by
/// <see cref="BalanceSuggestion.EventName"/> rather than <see cref="BalanceSuggestion.ClassName"/>.
/// </summary>
public class EventsXmlApplyService
{
    public ApplyResult Apply(IEnumerable<BalanceSuggestion> approved, string eventsXmlPath, bool backup)
    {
        var result = new ApplyResult();

        if (!File.Exists(eventsXmlPath))
        {
            result.Errors.Add($"events.xml not found: {eventsXmlPath}");
            return result;
        }

        if (backup)
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var backupPath = $"{eventsXmlPath}.backup.{stamp}";
                File.Copy(eventsXmlPath, backupPath, overwrite: false);
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
            doc = XDocument.Load(eventsXmlPath, LoadOptions.PreserveWhitespace);
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

        // Index <event name="..."> elements once for O(1) lookup.
        var index = root.Elements("event")
            .Where(e => e.Attribute("name") != null)
            .GroupBy(e => e.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var sug in approved)
        {
            if (sug == null || !sug.IsApproved || sug.Changes.Count == 0) continue;
            if (sug.Target != SuggestionTarget.EventsXml) continue; // types.xml handled by XmlApplyService

            if (string.IsNullOrWhiteSpace(sug.EventName))
            {
                result.Errors.Add($"{sug.ClassName}: no event name recorded, cannot apply to events.xml.");
                continue;
            }

            if (!index.TryGetValue(sug.EventName, out var eventEl))
            {
                result.NotFound++;
                continue;
            }

            try
            {
                foreach (var kv in sug.Changes)
                    SetChildIntValue(eventEl, kv.Key, kv.Value.NewValue);
                result.Applied++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{sug.EventName}: {ex.Message}");
            }
        }

        try
        {
            doc.Save(eventsXmlPath, SaveOptions.DisableFormatting);
        }
        catch (Exception ex)
        {
            result.Errors.Add("XML save failed: " + ex.Message);
        }

        return result;
    }

    private static void SetChildIntValue(XElement eventEl, string childName, int value)
    {
        var child = eventEl.Element(childName);
        if (child == null)
        {
            child = new XElement(childName);
            eventEl.Add(child);
        }
        child.SetValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
