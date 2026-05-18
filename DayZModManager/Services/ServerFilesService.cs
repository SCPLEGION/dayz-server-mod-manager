using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DayZModManager.Models;

namespace DayZModManager.Services;

/// <summary>
/// Resolves the canonical set of DayZ server files (serverDZ.cfg, init.c, the economy XMLs) given
/// a server root and a mission folder. Acts as the registry the AI task planner and MCP server use
/// to read/write files in a structured way.
/// </summary>
public class ServerFilesService
{
    private static readonly (ServerFileKind Kind, ServerFileFormat Format, string RelPath, bool MissionScoped)[] Registry =
    {
        (ServerFileKind.ServerCfg,         ServerFileFormat.Cfg,  "serverDZ.cfg",                                    false),
        (ServerFileKind.InitC,             ServerFileFormat.Text, "init.c",                                          true),
        (ServerFileKind.TypesXml,          ServerFileFormat.Xml,  "db/types.xml",                                    true),
        (ServerFileKind.EventsXml,         ServerFileFormat.Xml,  "db/events.xml",                                   true),
        (ServerFileKind.GlobalsXml,        ServerFileFormat.Xml,  "db/globals.xml",                                  true),
        (ServerFileKind.SpawnableTypesXml, ServerFileFormat.Xml,  "cfgspawnabletypes.xml",                           true),
        (ServerFileKind.CfgEconomyCoreXml, ServerFileFormat.Xml,  "cfgeconomycore.xml",                              true),
        (ServerFileKind.MessagesXml,       ServerFileFormat.Xml,  "db/messages.xml",                                 true),
        (ServerFileKind.CfgEventSpawnsXml, ServerFileFormat.Xml,  "cfgeventspawns.xml",                              true),
        (ServerFileKind.CfgGameplayJson,   ServerFileFormat.Text, "cfggameplay.json",                                true),
    };

    public ServerFilesSnapshot Discover(string serverRoot, string missionFolder)
    {
        var snap = new ServerFilesSnapshot
        {
            ServerRoot = serverRoot ?? string.Empty,
            MissionFolder = missionFolder ?? string.Empty,
        };

        foreach (var entry in Registry)
        {
            var absolute = ResolvePath(serverRoot, missionFolder, entry.RelPath, entry.MissionScoped);
            var info = new ServerFileInfo
            {
                Kind = entry.Kind,
                Format = entry.Format,
                RelativePath = entry.RelPath,
                IsMissionScoped = entry.MissionScoped,
                AbsolutePath = absolute,
            };
            if (!string.IsNullOrEmpty(absolute) && File.Exists(absolute))
            {
                info.Exists = true;
                try { info.SizeBytes = new FileInfo(absolute).Length; } catch { /* ignored */ }
            }
            snap.Files.Add(info);
        }
        return snap;
    }

    public string ResolvePath(string? serverRoot, string? missionFolder, string relativePath, bool missionScoped)
    {
        var baseDir = missionScoped ? missionFolder : serverRoot;
        if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;
        try { return Path.GetFullPath(Path.Combine(baseDir, relativePath)); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Looks up a registered file by kind in a snapshot. Returns null if not registered.
    /// </summary>
    public ServerFileInfo? Find(ServerFilesSnapshot snap, ServerFileKind kind) =>
        snap.Files.FirstOrDefault(f => f.Kind == kind);

    /// <summary>
    /// Reads file bytes as UTF-8 text. Throws on missing files (caller should check Exists first).
    /// </summary>
    public string ReadText(ServerFileInfo file) => File.ReadAllText(file.AbsolutePath);

    /// <summary>
    /// Writes text to disk, creating a timestamped .backup copy first when <paramref name="backup"/> is true.
    /// </summary>
    public string? WriteText(ServerFileInfo file, string contents, bool backup)
    {
        string? backupPath = null;
        if (string.IsNullOrEmpty(file.AbsolutePath))
            throw new InvalidOperationException("File not resolved.");
        var dir = Path.GetDirectoryName(file.AbsolutePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        if (backup && File.Exists(file.AbsolutePath))
        {
            backupPath = file.AbsolutePath + ".backup." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Copy(file.AbsolutePath, backupPath, overwrite: true);
        }
        File.WriteAllText(file.AbsolutePath, contents);
        return backupPath;
    }
}
