using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.XPath;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// Executes the approved <see cref="TaskAction"/> entries of a <see cref="TaskProposal"/> against
/// the resolved server files. Creates timestamped backups for every distinct file touched.
/// </summary>
public class TaskApplyService
{
    private readonly CfgFileService _cfg;

    public TaskApplyService(CfgFileService? cfg = null)
    {
        _cfg = cfg ?? new CfgFileService();
    }

    public TaskApplyResult Apply(TaskProposal proposal, bool backup)
    {
        var result = new TaskApplyResult();
        if (proposal?.Actions is null) return result;

        // Group approved actions by target file so backups are made once per file.
        var approved = proposal.Actions.Where(a => a.IsApproved).ToList();
        var byFile = approved
            .Where(a => !string.IsNullOrEmpty(a.TargetFile))
            .GroupBy(a => a.TargetFile!, StringComparer.OrdinalIgnoreCase);

        foreach (var grp in byFile)
        {
            var path = grp.Key;
            if (!File.Exists(path))
            {
                result.Skipped += grp.Count();
                result.Errors.Add($"File not found: {path}");
                continue;
            }

            string? backupPath = null;
            if (backup)
            {
                try
                {
                    backupPath = path + ".backup." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(path, backupPath, overwrite: true);
                    result.BackupPaths.Add(backupPath);
                }
                catch (Exception ex) { result.Errors.Add($"Backup failed for {path}: {ex.Message}"); }
            }

            foreach (var action in grp)
            {
                try
                {
                    switch (action.Kind)
                    {
                        case TaskActionKind.CfgSet:
                            ApplyCfgSet(path, action);
                            result.Applied++;
                            break;
                        case TaskActionKind.XmlSetValue:
                            ApplyXmlSetValue(path, action);
                            result.Applied++;
                            break;
                        case TaskActionKind.TextReplace:
                            ApplyTextReplace(path, action);
                            result.Applied++;
                            break;
                        default:
                            result.Skipped++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{Path.GetFileName(path)}: {action.Summary} – {ex.Message}");
                    result.Skipped++;
                }
            }
        }

        // Handle non-file actions (RunBalance / Note) — they are reported back but not executed here.
        foreach (var action in approved.Where(a => string.IsNullOrEmpty(a.TargetFile)))
        {
            if (action.Kind == TaskActionKind.RunBalance || action.Kind == TaskActionKind.Note)
                result.Skipped++; // tracked separately; caller decides
        }
        return result;
    }

    private void ApplyCfgSet(string path, TaskAction action)
    {
        if (string.IsNullOrEmpty(action.Key)) throw new InvalidOperationException("CfgSet requires Key.");
        var text = File.ReadAllText(path);
        var updated = _cfg.SetKey(text, action.Key!, action.NewValue ?? string.Empty, out _);
        File.WriteAllText(path, updated);
    }

    private static void ApplyXmlSetValue(string path, TaskAction action)
    {
        if (string.IsNullOrEmpty(action.XPath)) throw new InvalidOperationException("XmlSetValue requires XPath.");
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);
        var nav = doc.CreateNavigator() ?? throw new InvalidOperationException("Cannot navigate XML.");
        var node = nav.SelectSingleNode(action.XPath!);
        if (node is null) throw new InvalidOperationException($"XPath matched no node: {action.XPath}");
        node.SetValue(action.NewValue ?? string.Empty);
        doc.Save(path);
    }

    private static void ApplyTextReplace(string path, TaskAction action)
    {
        if (string.IsNullOrEmpty(action.OldText)) throw new InvalidOperationException("TextReplace requires OldText.");
        var text = File.ReadAllText(path);
        var idx = text.IndexOf(action.OldText!, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("OldText not found in file.");
        var lastIdx = text.LastIndexOf(action.OldText!, StringComparison.Ordinal);
        if (lastIdx != idx) throw new InvalidOperationException("OldText is ambiguous (matches more than once).");
        var replaced = text.Substring(0, idx) + (action.NewValue ?? string.Empty) + text.Substring(idx + action.OldText!.Length);
        File.WriteAllText(path, replaced);
    }
}
