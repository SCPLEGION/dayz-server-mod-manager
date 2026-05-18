using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DayZModManager.Services;

/// <summary>
/// Reads / edits a DayZ <c>serverDZ.cfg</c>-style file.
/// Format is line-based: <c>key = value;</c> with <c>//</c> comments. Values may be strings (quoted),
/// integers, floats, or arrays (rare). This service preserves all original lines, comments, and
/// whitespace; only the matched assignment is rewritten in place.
/// </summary>
public class CfgFileService
{
    private static readonly Regex AssignRegex = new(
        @"^(?<indent>\s*)(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<val>.+?)\s*;\s*(?<trail>//.*)?$",
        RegexOptions.Compiled);

    public Dictionary<string, string> Parse(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return dict;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
            var m = AssignRegex.Match(line);
            if (!m.Success) continue;
            var key = m.Groups["key"].Value;
            var val = m.Groups["val"].Value.Trim();
            // Strip surrounding quotes for string values for ease of reading.
            if (val.Length >= 2 && val.StartsWith("\"") && val.EndsWith("\""))
                val = val.Substring(1, val.Length - 2);
            dict[key] = val;
        }
        return dict;
    }

    public Dictionary<string, string> ReadFile(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Rewrites <paramref name="key"/> = <paramref name="newValue"/>; in <paramref name="text"/>.
    /// Returns the new text. If the key is missing, it is appended at the end. Numeric values are
    /// written without quotes; everything else is quoted unless it parses cleanly as int/float.
    /// </summary>
    public string SetKey(string text, string key, string newValue, out bool changed)
    {
        changed = false;
        var formatted = FormatValue(newValue);
        var lines = (text ?? string.Empty).Split('\n').ToList();
        var keyFound = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');
            var m = AssignRegex.Match(line);
            if (!m.Success) continue;
            if (!string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase)) continue;

            keyFound = true;
            var indent = m.Groups["indent"].Value;
            var trail = m.Groups["trail"].Success ? "  " + m.Groups["trail"].Value : "";
            var rebuilt = $"{indent}{key} = {formatted};{trail}";
            if (rebuilt != line)
            {
                changed = true;
                lines[i] = rebuilt;
            }
            break;
        }

        if (!keyFound)
        {
            lines.Add($"{key} = {formatted};");
            changed = true;
        }
        return string.Join("\n", lines);
    }

    /// <summary>Applies a set of key/value changes to the file at <paramref name="path"/> and writes a .backup copy first.</summary>
    public CfgApplyResult ApplyChanges(string path, IReadOnlyDictionary<string, string> changes, bool backup)
    {
        var result = new CfgApplyResult();
        if (!File.Exists(path))
        {
            result.Errors.Add("File not found: " + path);
            return result;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (backup)
            {
                var backupPath = path + ".backup." + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                File.Copy(path, backupPath, overwrite: true);
                result.BackupPath = backupPath;
            }

            foreach (var (k, v) in changes)
            {
                text = SetKey(text, k, v, out var ch);
                if (ch) result.Applied++;
                else result.Skipped++;
            }
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }
        return result;
    }

    private static string FormatValue(string raw)
    {
        if (raw is null) return "\"\"";
        var trimmed = raw.Trim();
        // Already quoted?
        if (trimmed.Length >= 2 && trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
            return trimmed;
        // Pure numeric (int or float)?
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return trimmed;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return trimmed;
        // Otherwise quote and escape any embedded quotes.
        return "\"" + trimmed.Replace("\"", "\\\"") + "\"";
    }
}

public class CfgApplyResult
{
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? BackupPath { get; set; }
}
