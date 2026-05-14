using System.Collections.Generic;
using System.Linq;
using DayZModManager.Services;

namespace DayZModManager;

/// <summary>
/// Backward-compatible thin wrapper around <see cref="XmlMergeService"/> using the
/// built-in "types" preset. New code should call <see cref="XmlMergeService"/> directly.
/// </summary>
internal static class TypesXmlGenerator
{
    public sealed record MergeConflict(string Key, string? ExistingSourceFile, string NewSourceFile);
    public sealed record MergeStats(
        int ModDirsScanned,
        int TypesXmlFilesFound,
        int CandidateTypesElementsFound,
        int MergedTypeChildren,
        int UniqueTypeKeys,
        int ConflictCount,
        List<MergeConflict> Conflicts
    );

    private static XmlMergePreset TypesPreset =>
        XmlMergePresets.FindById("types")
        ?? throw new System.InvalidOperationException("Built-in 'types' preset missing.");

    public static void Generate(string modsRoot, string outFile) =>
        Generate(modsRoot, outFile, TypesXmlMergeMode.DedupeFirstByKey);

    public static void Generate(string modsRoot, string outFile, TypesXmlMergeMode mergeMode)
    {
        XmlMergeService.Generate(modsRoot, TypesPreset, mergeMode, outFile);
    }

    public static MergeStats Preview(string modsRoot, TypesXmlMergeMode mergeMode)
    {
        var stats = XmlMergeService.Preview(modsRoot, TypesPreset, mergeMode);
        return new MergeStats(
            ModDirsScanned: stats.ModDirsScanned,
            TypesXmlFilesFound: stats.FilesFound,
            CandidateTypesElementsFound: stats.CandidateChildrenFound,
            MergedTypeChildren: stats.MergedChildren,
            UniqueTypeKeys: stats.UniqueKeys,
            ConflictCount: stats.ConflictCount,
            Conflicts: stats.Conflicts
                .Select(c => new MergeConflict(c.Key, c.ExistingSource, c.NewSource))
                .ToList()
        );
    }
}
