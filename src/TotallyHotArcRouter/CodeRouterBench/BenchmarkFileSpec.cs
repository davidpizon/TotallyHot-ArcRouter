namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// One entry of the "Files synchronized" table in
/// docs/router/coderouterbench-sqlite-migration-plan.md: a file's name, which table/split it lands in,
/// how many data rows it must contain, and which importer parses it.
/// </summary>
/// <param name="FileName">The file's name in the published Hugging Face dataset.</param>
/// <param name="Kind">Which importer parses this file's bytes.</param>
/// <param name="Split">
/// The <c>split</c> discriminator value written for every row imported from this file, or
/// <see langword="null"/> for files whose target table carries no split column (the OOD tables carry
/// only one split each).
/// </param>
/// <param name="ExpectedRowCount">
/// The exact data-row count this file must contain, asserted after parsing and before the import
/// transaction commits - matching the row-count assertions the now-removed
/// <c>scripts/fetch-coderouterbench.sh</c> used to run.
/// <see langword="null"/> for files with no fixed row count. None currently use <see langword="null"/>:
/// <c>models.json</c> publishes exactly 8 model entries and <c>summary.json</c> exactly 5 keys.
/// </param>
public sealed record BenchmarkFileSpec(string FileName, BenchmarkFileKind Kind, string? Split, int? ExpectedRowCount)
{
    /// <summary>
    /// The eight files Phase 2 downloads and imports, in the order
    /// docs/router/coderouterbench-sqlite-migration-plan.md's "Files synchronized" table lists them.
    /// Deliberately excludes the two derived files (<c>id_results_long.csv</c>, <c>id_tasks.jsonl</c>) -
    /// see the plan's "Derived files" section for why they are reconstructed by query, never downloaded.
    /// </summary>
    public static readonly IReadOnlyList<BenchmarkFileSpec> All =
    [
        new(FileName: "id_probing_results_long.csv", Kind: BenchmarkFileKind.IdResultsCsv, Split: "probing", 56_640),
        new(FileName: "id_test_results_long.csv", Kind: BenchmarkFileKind.IdResultsCsv, Split: "id_test", 23_352),
        new(FileName: "ood176_results_long.csv", Kind: BenchmarkFileKind.OodResultsCsv, null, 1_408),
        new(FileName: "id_probing_tasks.jsonl", Kind: BenchmarkFileKind.IdTasksJsonl, Split: "probing", 7_080),
        new(FileName: "id_test_tasks.jsonl", Kind: BenchmarkFileKind.IdTasksJsonl, Split: "id_test", 2_919),
        new(FileName: "ood176_tasks.jsonl", Kind: BenchmarkFileKind.OodTasksJsonl, null, 176),
        new(FileName: "models.json", Kind: BenchmarkFileKind.ModelsJson, null, 8),
        new(FileName: "summary.json", Kind: BenchmarkFileKind.SummaryJson, null, 5)
    ];
}