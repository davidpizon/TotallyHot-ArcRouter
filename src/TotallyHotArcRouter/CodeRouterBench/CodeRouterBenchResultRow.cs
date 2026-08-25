namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// A single task/model result row from one of the CodeRouterBench <c>*_results_long.csv</c> tables
/// (docs/router/coderouterbench-sqlite-migration-plan.md), synced on demand from
/// <see href="https://huggingface.co/datasets/Lance1573/CodeRouterBench"/> into <c>benchmark_id_results</c>.
/// Carries only the columns the dimension x model score matrix needs; the source CSVs also carry cost
/// and token columns, read separately once a consumer needs them.
/// </summary>
/// <param name="TaskId">The benchmark task identifier.</param>
/// <param name="Dimension">
/// The coding dimension key (research-doc §4.4), matching <see cref="Quality.RouterDimension"/>'s
/// vocabulary except for <c>"algorithm"</c>, which the released CSVs use where the router's own
/// vocabulary says <see cref="Quality.RouterDimension.AlgorithmDesign"/> - see
/// <see cref="CodeRouterBenchCsvReader.NormalizeDimension"/>.
/// </param>
/// <param name="Model">
/// The backend model identifier in canonical comparison form - the dataset's own <c>models.json</c>
/// spelling mapped through <see cref="Models.ModelNameCanonicalizer.Canonicalize"/>, so that a row loaded
/// from <c>MiniMax-M2.7</c> and a configured <c>ModelName</c> of <c>minimax-m2.7</c> compare equal.
/// </param>
/// <param name="Score">The recorded score for this task/model pair, in <c>[0, 1]</c>.</param>
public sealed record CodeRouterBenchResultRow(string TaskId, string Dimension, string Model, double Score);
