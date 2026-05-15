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
    IReadOnlyList<string> DeployedAtNames
);
