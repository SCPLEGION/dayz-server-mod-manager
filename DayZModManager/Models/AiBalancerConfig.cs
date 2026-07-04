namespace DayZModManager.Models;

public class AiBalancerConfig
{
    public int ListenerPort { get; set; } = 7823;
    public string ListenerSecret { get; set; } = string.Empty;
    /// <summary>DPAPI-encrypted (base64) blob — decrypt with ApiKeyProtection.Unprotect.</summary>
    public string OpenAiApiKeyEncrypted { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "gpt-5.4-nano-2026-03-17";
    public int Concurrency { get; set; } = 3;
    public int BatchSize { get; set; } = 30;
    public string ServerType { get; set; } = "PvE";
    public string TypesXmlPath { get; set; } = string.Empty;
    public string EventsXmlPath { get; set; } = string.Empty;
    public string GlobalsXmlPath { get; set; } = string.Empty;
    public string SpawnableTypesXmlPath { get; set; } = string.Empty;
    /// <summary>Root folder of the DayZ server install (used for whole-server file editing).</summary>
    public string ServerRootPath { get; set; } = string.Empty;
    /// <summary>Mission folder under the server (e.g. mpmissions/dayzOffline.chernarusplus).</summary>
    public string MissionPath { get; set; } = string.Empty;
    public bool BackupBeforeApply { get; set; } = true;
    public bool AutoStartListener { get; set; }

    /// <summary>
    /// Off by default: lets the MCP server's apply_balance/apply_task tools actually write to
    /// server files. propose_balance/plan_task always work regardless (propose-only, no writes).
    /// An external AI client can only apply changes once the user opts in here.
    /// </summary>
    public bool McpApplyEnabled { get; set; }

    // ---- Scheduled/automatic runs (only while this app's GUI process is alive) ----

    public bool ScheduleEnabled { get; set; }
    public int ScheduleIntervalMinutes { get; set; } = 60;
    public bool AutoApplyEnabled { get; set; }
    /// <summary>Auto-apply only touches suggestions whose |delta| stays within this percent of the old value.</summary>
    public double AutoApplyMaxDeltaPercent { get; set; } = 20;
}
