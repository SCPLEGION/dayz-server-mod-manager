using System.Collections.Generic;

namespace DayZModManager.Models;

public enum ServerFileKind
{
    ServerCfg,         // serverDZ.cfg
    InitC,             // mission/init.c
    TypesXml,
    EventsXml,
    GlobalsXml,
    SpawnableTypesXml,
    CfgEconomyCoreXml,
    MessagesXml,
    CfgEventSpawnsXml,
    CfgGameplayJson,
}

public enum ServerFileFormat
{
    Cfg,    // line-based key = value;
    Xml,
    Text,   // .c source files / json / freeform
}

public class ServerFileInfo
{
    public ServerFileKind Kind { get; set; }
    public ServerFileFormat Format { get; set; }
    /// <summary>Path relative to server root or mission folder.</summary>
    public string RelativePath { get; set; } = string.Empty;
    /// <summary>True if the file lives under the mission folder; false if under the server root.</summary>
    public bool IsMissionScoped { get; set; }
    /// <summary>Fully-resolved absolute path (populated after the registry resolves it).</summary>
    public string AbsolutePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
}

public class ServerFilesSnapshot
{
    public string ServerRoot { get; set; } = string.Empty;
    public string MissionFolder { get; set; } = string.Empty;
    public List<ServerFileInfo> Files { get; set; } = new();
}
