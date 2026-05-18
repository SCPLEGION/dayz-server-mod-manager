using System.Collections.Generic;

namespace DayZModManager.Services;

internal sealed record ServerLaunchSpec(
    ServerLaunchMode Mode,
    string? Ps1Path,
    string? ExePath,
    string? ProfileDir,
    string? ServerRootDir,
    int? Port,
    string? ExtraArgs,
    bool BattlEye,
    IReadOnlyList<string> DeployedAtNames,
    string Ps1LaunchParam = "default",
    string Ps1AppBranch = "stable",
    IReadOnlyList<string>? DeployedServerModNames = null
);
