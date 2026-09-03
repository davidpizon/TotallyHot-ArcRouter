namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Configuration for the CodeRouterBench corpus sync
/// (docs/router/coderouterbench-sqlite-migration-plan.md), bound from the <c>CodeRouterBench</c>
/// configuration section.
/// </summary>
public sealed class BenchmarkSyncOptions
{
    /// <summary>Gets the configuration section name.</summary>
    public const string SectionName = "CodeRouterBench";

    /// <summary>
    /// Gets the dataset ref (branch, tag, or commit) the sync tracks. Defaults to <c>"main"</c>,
    /// matching the now-removed <c>scripts/fetch-coderouterbench.sh</c>'s default and its
    /// <c>CODEROUTERBENCH_REF</c> override - see the migration plan's "Deliberately out of scope" section
    /// on pinning.
    /// </summary>
    public string DatasetRef { get; init; } = "main";
}