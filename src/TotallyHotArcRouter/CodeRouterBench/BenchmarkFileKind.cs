namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// The on-disk shape of one CodeRouterBench source file, selecting which importer
/// <see cref="BenchmarkSyncService"/> hands the downloaded bytes to.
/// </summary>
public enum BenchmarkFileKind
{
    /// <summary>An ID-schema results CSV, imported into <c>benchmark_id_results</c>.</summary>
    IdResultsCsv,

    /// <summary>The OOD results CSV, imported into <c>benchmark_ood_results</c>.</summary>
    OodResultsCsv,

    /// <summary>An ID-schema tasks JSONL file, imported into <c>benchmark_id_tasks</c>.</summary>
    IdTasksJsonl,

    /// <summary>The OOD tasks JSONL file, imported into <c>benchmark_ood_tasks</c>.</summary>
    OodTasksJsonl,

    /// <summary>The models catalog, imported into <c>benchmark_models</c>.</summary>
    ModelsJson,

    /// <summary>The summary document, imported into <c>benchmark_summary</c>.</summary>
    SummaryJson,
}
