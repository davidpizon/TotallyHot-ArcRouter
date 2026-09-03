using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Reads <c>benchmark_models</c> pricing and resolves a fallback cost when a result row's own
/// <c>cost_usd</c> is null - shared by <see cref="OodRegretTaskOutcomeLoader"/> and
/// <see cref="IdSplitRegretTaskOutcomeLoader"/> so both splits fall back to cost identically, per
/// <see cref="RegretOutcomeCell"/>'s documented "cost falling back to benchmark_models pricing" contract.
/// </summary>
public static class BenchmarkModelPricingLookup
{
    /// <summary>Loads every model's per-million-token input/output pricing, keyed by canonicalized model id.</summary>
    /// <param name="connection">An open connection to a <see cref="BenchmarkDatabase"/>.</param>
    public static IReadOnlyDictionary<string, (double InputPer1M, double OutputPer1M)> Load(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var pricing = new Dictionary<string, (double InputPer1M, double OutputPer1M)>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model, input_per_1m, output_per_1m FROM benchmark_models;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;

            var model = ModelNameCanonicalizer.Canonicalize(reader.GetString(0));
            pricing[model] = (reader.GetDouble(1), reader.GetDouble(2));
        }

        return pricing;
    }

    /// <summary>
    /// Resolves a fallback cost in USD from per-million-token pricing over a result row's own token counts,
    /// used when the row's own <c>cost_usd</c> is null.
    /// </summary>
    /// <param name="model">The canonicalized model id.</param>
    /// <param name="inputTokens">The row's input token count, or <see langword="null"/> when unpublished.</param>
    /// <param name="outputTokens">The row's output token count, or <see langword="null"/> when unpublished.</param>
    /// <param name="pricing">
    /// Per-model input/output per-million-token pricing, keyed by canonicalized model id, e.g. from
    /// <see cref="Load"/>.
    /// </param>
    /// <returns>The resolved cost in USD, or <see langword="null"/> when no pricing or token counts are available.</returns>
    public static double? ResolveFallbackCostUsd(
        string model,
        long? inputTokens,
        long? outputTokens,
        IReadOnlyDictionary<string, (double InputPer1M, double OutputPer1M)> pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);

        if (inputTokens is null && outputTokens is null) return null;

        if (!pricing.TryGetValue(key: model, value: out var price)) return null;

        return (inputTokens ?? 0) * price.InputPer1M / 1_000_000.0 +
               (outputTokens ?? 0) * price.OutputPer1M / 1_000_000.0;
    }
}