using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Builds <see cref="RegretTaskOutcome"/> rows — with <see cref="RegretTaskOutcome.TaskText"/> populated —
/// from the CodeRouterBench OOD split, for replaying the text-limited baselines (<see cref="LogRegBaseline"/>,
/// <see cref="KnnRetrievalBaseline"/>) and every other baseline against real data
/// (docs/router/regret-evaluation-harness-plan.md N4/N5). The ID-test split publishes no task text and is
/// therefore loaded separately (N5), not by this type.
/// </summary>
public static class OodRegretTaskOutcomeLoader
{
    /// <summary>
    /// Loads every OOD task that has at least one <c>resolved</c>-non-null result row, joining each cell's
    /// score, cost, and token counts from <c>benchmark_ood_results</c>.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus to read the OOD split from.</param>
    /// <returns>One <see cref="RegretTaskOutcome"/> per OOD task with at least one usable cell.</returns>
    /// <exception cref="InvalidOperationException">The corpus database is not synced.</exception>
    /// <remarks>
    /// <b>Score.</b> <c>benchmark_ood_results</c> carries no continuous <c>score</c> column, only
    /// <c>resolved</c> - the same constraint <see cref="LogRegTrainer"/> works around - so
    /// <see cref="RegretOutcomeCell.Score"/> is <c>1.0</c> when resolved, <c>0.0</c> otherwise. A row with
    /// <c>resolved IS NULL</c> has no well-defined score and is excluded rather than assigned an arbitrary
    /// one. <b>Cost.</b> Uses the row's own <c>cost_usd</c> when present; when absent, falls back to
    /// <c>benchmark_models</c> pricing over the row's token counts, matching
    /// <see cref="RegretOutcomeCell"/>'s documented "cost falling back to benchmark_models pricing" contract.
    /// A row with neither is excluded, since <see cref="RegretOutcomeCell.CostUsd"/> is never a raw-null
    /// passthrough.
    /// </remarks>
    public static IReadOnlyList<RegretTaskOutcome> Load(BenchmarkDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!File.Exists(database.DatabasePath))
        {
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{database.DatabasePath}' - is the corpus synced?");
        }

        using var connection = database.OpenConnection();

        var taskText = new Dictionary<string, string>(StringComparer.Ordinal);
        var taskDimension = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var tasksCommand = connection.CreateCommand())
        {
            tasksCommand.CommandText = "SELECT task_id, dimension, raw_json FROM benchmark_ood_tasks;";
            using var reader = tasksCommand.ExecuteReader();
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                taskDimension[taskId] = reader.GetString(1);

                var text = LogRegTrainer.TryExtractPrompt(reader.GetString(2));
                if (text is not null)
                {
                    taskText[taskId] = text;
                }
            }
        }

        var modelPricing = BenchmarkModelPricingLookup.Load(connection);

        var cellsByTask = new Dictionary<string, Dictionary<string, RegretOutcomeCell>>(StringComparer.Ordinal);
        using (var resultsCommand = connection.CreateCommand())
        {
            resultsCommand.CommandText =
                "SELECT task_id, dimension, model, resolved, in_tok, out_tok, cost_usd FROM benchmark_ood_results;";
            using var reader = resultsCommand.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(3))
                {
                    // No well-defined score for this cell - excluded rather than assigned an arbitrary one.
                    continue;
                }

                var taskId = reader.GetString(0);
                if (!taskDimension.ContainsKey(taskId))
                {
                    taskDimension[taskId] = reader.GetString(1);
                }

                var model = ModelNameCanonicalizer.Canonicalize(reader.GetString(2));
                var resolved = reader.GetInt32(3) != 0;
                long? inTok = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                long? outTok = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                var costUsd = reader.IsDBNull(6)
                    ? BenchmarkModelPricingLookup.ResolveFallbackCostUsd(model, inTok, outTok, modelPricing)
                    : reader.GetDouble(6);

                if (costUsd is null)
                {
                    // Neither the row's own cost_usd nor benchmark_models pricing over its token counts
                    // resolved a cost - RegretOutcomeCell.CostUsd is never a raw-null passthrough, so this
                    // cell is excluded rather than assigned a misleading zero.
                    continue;
                }

                long? totalTokens = inTok is null && outTok is null ? null : (inTok ?? 0) + (outTok ?? 0);

                if (!cellsByTask.TryGetValue(taskId, out var cells))
                {
                    cells = new Dictionary<string, RegretOutcomeCell>(StringComparer.Ordinal);
                    cellsByTask[taskId] = cells;
                }

                cells[model] = new RegretOutcomeCell(resolved ? 1.0 : 0.0, costUsd.Value, totalTokens);
            }
        }

        var outcomes = new List<RegretTaskOutcome>(cellsByTask.Count);
        foreach (var (taskId, cells) in cellsByTask)
        {
            if (cells.Count == 0)
            {
                continue;
            }

            var dimension = taskDimension.TryGetValue(taskId, out var dim) ? dim : string.Empty;
            outcomes.Add(new RegretTaskOutcome(taskId, dimension, cells, taskText.TryGetValue(taskId, out var text) ? text : null));
        }

        return outcomes;
    }
}
