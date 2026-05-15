using System.Text.Json.Serialization;

namespace DayZModManager;

public enum ServerLaunchMode
{
    Ps1,
    DirectExe
}

public enum SteamLoginMode
{
    Anonymous,
    Interactive
}

public enum ModDeployMode
{
    Junction,
    Symlink,
    Copy
}

public sealed class ServerConfig
{
    [JsonPropertyName("mode")]
    public ServerLaunchMode Mode { get; set; } = ServerLaunchMode.DirectExe;

    [JsonPropertyName("ps1Path")]
    public string? Ps1Path { get; set; }

    [JsonPropertyName("exePath")]
    public string? ExePath { get; set; }

    [JsonPropertyName("profileDir")]
    public string? ProfileDir { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("extraArgs")]
    public string? ExtraArgs { get; set; }

    [JsonPropertyName("battlEye")]
    public bool BattlEye { get; set; } = true;

    [JsonPropertyName("runPreStartMerge")]
    public bool RunPreStartMerge { get; set; }

    [JsonPropertyName("preStartPresetId")]
    public string? PreStartPresetId { get; set; } = "types";

    [JsonPropertyName("autoRestartOnCrash")]
    public bool AutoRestartOnCrash { get; set; }

    [JsonPropertyName("autoRestartBackoffSeconds")]
    public int AutoRestartBackoffSeconds { get; set; } = 10;

    [JsonPropertyName("autoRestartMaxRetries")]
    public int AutoRestartMaxRetries { get; set; } = 3;

    [JsonPropertyName("logDirOverride")]
    public string? LogDirOverride { get; set; }

    [JsonPropertyName("tailLineCap")]
    public int TailLineCap { get; set; } = 2000;

    // ---- SteamCMD block (Mode B only) ----

    [JsonPropertyName("steamCmdPath")]
    public string? SteamCmdPath { get; set; }

    [JsonPropertyName("loginMode")]
    public SteamLoginMode LoginMode { get; set; } = SteamLoginMode.Interactive;

    [JsonPropertyName("steamLogin")]
    public string? SteamLogin { get; set; }

    [JsonPropertyName("modCacheDir")]
    public string? ModCacheDir { get; set; }

    [JsonPropertyName("serverRootDir")]
    public string? ServerRootDir { get; set; }

    [JsonPropertyName("workshopAppId")]
    public uint WorkshopAppId { get; set; } = 221100;

    [JsonPropertyName("deployMode")]
    public ModDeployMode DeployMode { get; set; } = ModDeployMode.Junction;

    [JsonPropertyName("autoUpdateModsBeforeStart")]
    public bool AutoUpdateModsBeforeStart { get; set; }

    [JsonPropertyName("validateMods")]
    public bool ValidateMods { get; set; }
}
