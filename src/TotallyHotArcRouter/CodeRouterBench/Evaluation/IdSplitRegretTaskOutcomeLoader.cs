using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Builds <see cref="RegretTaskOutcome"/> rows from a CodeRouterBench <c>benchmark_id_results</c> split
/// (<c>"probing"</c> or <c>"id_test"</c>) for N5's Orchestrator arm and comparison report
/// (docs/router/regret-evaluation-harness-plan.md). Every row's <see cref="RegretTaskOutcome.TaskText"/> is
/// <see langword="null"/> - the ID splits publish no task text at all (<see cref="LogRegTrainer"/>'s
/// remarks), unlike <see cref="OodRegretTaskOutcomeLoader"/>'s OOD split.
/// </summary>
public static class IdSplitRegretTaskOutcomeLoader
{
    /// <summary>
    /// Loads every task in <paramref name="split"/>, joining each cell's score, cost, and token counts from
    /// <c>benchmark_id_results</c>. Unlike the OOD split, <c>benchmark_id_results.score</c> is already a
    /// continuous <c>[0,1]</c> column - no <c>resolved</c>-to-score conversion is needed here.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus to read from.</param>
    /// <param name="split">The <c>split</c> value to filter on: <c>"probing"</c> or <c>"id_test"</c>.</param>
    /// <returns>One <see cref="RegretTaskOutcome"/> per task in <paramref name="split"/> with at least one usable cell.</returns>
    /// <exception cref="InvalidOperationException">The corpus database is not synced.</exception>
    /// <remarks>
    /// <b>Cost.</b> Uses each row's own <c>cost_usd</c> when present; when absent, falls back to
    /// <c>benchmark_models</c> pricing over the row's own <c>input_tokens</c>/<c>output_tokens</c>, matching
    /// <see cref="RegretOutcomeCell"/>'s documented "cost falling back to benchmark_models pricing" contract
    /// (the same fallback <see cref="OodRegretTaskOutcomeLoader"/> applies via
    /// <see cref="BenchmarkModelPricingLookup"/>). A row with neither is excluded, since
    /// <see cref="RegretOutcomeCell.CostUsd"/> is never a raw-null passthrough. <b>Tokens.</b>
    /// <c>benchmark_id_results.total_tokens</c> is read directly rather than summed from its input/output
    /// halves, since the ID splits publish it as its own column.
    /// </remarks>
    public static IReadOnlyList<RegretTaskOutcome> Load(BenchmarkDatabase database, string split)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);

        if (!File.Exists(database.DatabasePath))
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{database.DatabasePath}' - is the corpus synced?");

        using var connection = database.OpenConnection();

        var modelPricing = BenchmarkModelPricingLookup.Load(connection);

        var taskDimension = new Dictionary<string, string>(StringComparer.Ordinal);
        var cellsByTask = new Dictionary<string, Dictionary<string, RegretOutcomeCell>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT task_id, dimension, model, score, cost_usd, input_tokens, output_tokens, total_tokens " +
                "FROM benchmark_id_results WHERE split = $split;";
            command.Parameters.AddWithValue(parameterName: "$split", value: split);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                taskDimension[taskId] = reader.GetString(1);

                var model = ModelNameCanonicalizer.Canonicalize(reader.GetString(2));
                var score = reader.GetDouble(3);
                long? inTok = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                long? outTok = reader.IsDBNull(6) ? null : reader.GetInt64(6);
                long? totalTokens = reader.IsDBNull(7) ? null : reader.GetInt64(7);
                var costUsd = reader.IsDBNull(4)
                    ? BenchmarkModelPricingLookup.ResolveFallbackCostUsd(model: model, inputTokens: inTok,
                        outputTokens: outTok, pricing: modelPricing)
                    : reader.GetDouble(4);

                if (costUsd is null)
                    // Neither the row's own cost_usd nor benchmark_models pricing over its token counts
                    // resolved a cost - RegretOutcomeCell.CostUsd is never a raw-null passthrough, so this
                    // cell is excluded rather than assigned a misleading zero.
                    continue;

                if (!cellsByTask.TryGetValue(key: taskId, value: out var cells))
                {
                    cells = new Dictionary<string, RegretOutcomeCell>(StringComparer.Ordinal);
                    cellsByTask[taskId] = cells;
                }

                cells[model] = new RegretOutcomeCell(Score: score, CostUsd: costUsd.Value, TotalTokens: totalTokens);
            }
        }

        var outcomes = new List<RegretTaskOutcome>(cellsByTask.Count);
        foreach (var (taskId, cells) in cellsByTask)
        {
            if (cells.Count == 0) continue;

            outcomes.Add(new RegretTaskOutcome(TaskId: taskId, Dimension: taskDimension[taskId], Cells: cells, null));
        }

        return outcomes;
    }
}