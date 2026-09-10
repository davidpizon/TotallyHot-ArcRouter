using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// A SQLite-backed <see cref="ITaxonomyComparisonStore"/> over <see cref="TranscriptDatabase"/>'s
/// <c>taxonomy_comparisons</c> table (docs/router/self-organizing-classification-plan.md Phase T4).
/// </summary>
public sealed class SqliteTaxonomyComparisonStore : ITaxonomyComparisonStore
{
    private readonly TranscriptDatabase _database;
    private readonly TranscriptOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteTaxonomyComparisonStore"/> class.
    /// </summary>
    /// <param name="database">The database holding both the transcripts and their comparisons.</param>
    /// <param name="options">Supplies <see cref="TranscriptOptions.Enabled"/>.</param>
    public SqliteTaxonomyComparisonStore(TranscriptDatabase database, IOptions<TranscriptOptions> options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<long>> LoadPendingComparisonsAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled) return Task.FromResult<IReadOnlyList<long>>([]);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT t.id
                              FROM request_transcripts t
                              LEFT JOIN taxonomy_comparisons c ON c.transcript_id = t.id
                              WHERE t.score IS NOT NULL AND t.memory_entry_id IS NOT NULL AND t.dimension IS NOT NULL
                                AND c.transcript_id IS NULL
                              ORDER BY t.id ASC
                              LIMIT $limit;
                              """;
        command.Parameters.AddWithValue(parameterName: "$limit", value: limit);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));

        return Task.FromResult<IReadOnlyList<long>>(ids);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// First-write-wins: <c>ON CONFLICT(transcript_id) DO NOTHING</c> makes an existing row immutable.
    /// The baseline model, its predicted score, and the four cost ingredients are all frozen-baseline
    /// values that must not silently drift if this row is ever recomputed by a rescan or backfill
    /// (docs/router/routing-roi-regret-plan.md's frozen-baseline correction) - both the token averages
    /// and the price catalog move as live traffic accumulates and prices refresh, so a later recomputation
    /// would price against inputs that were not actually in force when the row was first compared.
    /// </remarks>
    public Task UpsertAsync(TaxonomyComparisonRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled) return Task.CompletedTask;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO taxonomy_comparisons (
                                  transcript_id, compared_at_utc, session_id, observed_score, dimension_predicted_score,
                                  cluster_predicted_score, dimension_abs_error, cluster_abs_error, is_clustered,
                                  is_exploratory, routed_model, baseline_model, actual_cost_usd,
                                  baseline_estimated_cost_usd, estimated_net_savings_usd,
                                  baseline_predicted_score, estimated_regret, baseline_input_tokens,
                                  baseline_output_tokens, baseline_input_price_per_million,
                                  baseline_output_price_per_million)
                              VALUES (
                                  $transcriptId, $comparedAtUtc, $sessionId, $observedScore, $dimensionPredicted,
                                  $clusterPredicted, $dimensionError, $clusterError, $isClustered,
                                  $isExploratory, $routedModel, $baselineModel, $actualCost,
                                  $baselineCost, $netSavings, $baselinePredicted, $estimatedRegret,
                                  $baselineInputTokens, $baselineOutputTokens, $baselineInputPrice,
                                  $baselineOutputPrice)
                              ON CONFLICT(transcript_id) DO NOTHING;
                              """;
        command.Parameters.AddWithValue(parameterName: "$transcriptId", value: record.TranscriptId);
        command.Parameters.AddWithValue(parameterName: "$comparedAtUtc",
            value: record.ComparedAtUtc.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$sessionId", value: record.SessionId);
        command.Parameters.AddWithValue(parameterName: "$observedScore", value: record.ObservedScore);
        command.Parameters.AddWithValue(parameterName: "$dimensionPredicted",
            value: (object?)record.DimensionPredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$clusterPredicted",
            value: (object?)record.ClusterPredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$dimensionError",
            value: (object?)record.DimensionAbsoluteError ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$clusterError",
            value: (object?)record.ClusterAbsoluteError ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$isClustered", value: record.IsClustered ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$isExploratory", value: record.IsExploratory ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$routedModel", value: record.RoutedModel);
        command.Parameters.AddWithValue(parameterName: "$baselineModel",
            value: (object?)record.BaselineModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$actualCost",
            value: record.ActualCostUsd is { } actual ? (double)actual : DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$baselineCost",
            value: record.BaselineEstimatedCostUsd is { } baseline ? (double)baseline : DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$netSavings",
            value: record.EstimatedNetSavingsUsd is { } savings ? (double)savings : DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$baselinePredicted",
            value: (object?)record.BaselinePredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$estimatedRegret",
            value: (object?)record.EstimatedRegret ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$baselineInputTokens",
            value: (object?)record.BaselineInputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$baselineOutputTokens",
            value: (object?)record.BaselineOutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$baselineInputPrice",
            value: record.BaselineInputPricePerMillion is { } inputPrice ? (double)inputPrice : DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$baselineOutputPrice",
            value: record.BaselineOutputPricePerMillion is { } outputPrice ? (double)outputPrice : DBNull.Value);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TaxonomyComparisonRecord>> LoadSinceAsync(
        DateTimeOffset since,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled) return Task.FromResult<IReadOnlyList<TaxonomyComparisonRecord>>([]);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  transcript_id, compared_at_utc, session_id, observed_score, dimension_predicted_score,
                                  cluster_predicted_score, dimension_abs_error, cluster_abs_error, is_clustered,
                                  is_exploratory, routed_model, baseline_model, actual_cost_usd,
                                  baseline_estimated_cost_usd, estimated_net_savings_usd,
                                  baseline_predicted_score, estimated_regret, baseline_input_tokens,
                                  baseline_output_tokens, baseline_input_price_per_million,
                                  baseline_output_price_per_million
                              FROM taxonomy_comparisons
                              WHERE compared_at_utc >= $since AND ($sessionId IS NULL OR session_id = $sessionId)
                              ORDER BY compared_at_utc ASC, transcript_id ASC;
                              """;
        command.Parameters.AddWithValue(parameterName: "$since",
            value: since.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$sessionId", value: (object?)sessionId ?? DBNull.Value);

        var rows = new List<TaxonomyComparisonRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));

        return Task.FromResult<IReadOnlyList<TaxonomyComparisonRecord>>(rows);
    }

    /// <summary>Materializes one row into a <see cref="TaxonomyComparisonRecord"/>.</summary>
    /// <param name="reader">A reader positioned on a row selecting this table's columns in declaration order.</param>
    /// <returns>The materialized record.</returns>
    private static TaxonomyComparisonRecord Read(SqliteDataReader reader)
    {
        return new TaxonomyComparisonRecord(
            TranscriptId: reader.GetInt64(0),
            ComparedAtUtc: DateTimeOffset.Parse(input: reader.GetString(1),
                formatProvider: CultureInfo.InvariantCulture),
            SessionId: reader.GetString(2),
            ObservedScore: reader.GetDouble(3),
            DimensionPredictedScore: reader.IsDBNull(4) ? null : reader.GetDouble(4),
            ClusterPredictedScore: reader.IsDBNull(5) ? null : reader.GetDouble(5),
            DimensionAbsoluteError: reader.IsDBNull(6) ? null : reader.GetDouble(6),
            ClusterAbsoluteError: reader.IsDBNull(7) ? null : reader.GetDouble(7),
            IsClustered: reader.GetInt64(8) != 0,
            IsExploratory: reader.GetInt64(9) != 0,
            RoutedModel: reader.GetString(10),
            BaselineModel: reader.IsDBNull(11) ? null : reader.GetString(11),
            ActualCostUsd: reader.IsDBNull(12) ? null : (decimal)reader.GetDouble(12),
            BaselineEstimatedCostUsd: reader.IsDBNull(13) ? null : (decimal)reader.GetDouble(13),
            EstimatedNetSavingsUsd: reader.IsDBNull(14) ? null : (decimal)reader.GetDouble(14),
            BaselinePredictedScore: reader.IsDBNull(15) ? null : reader.GetDouble(15),
            EstimatedRegret: reader.IsDBNull(16) ? null : reader.GetDouble(16),
            BaselineInputTokens: reader.IsDBNull(17) ? null : reader.GetDouble(17),
            BaselineOutputTokens: reader.IsDBNull(18) ? null : reader.GetDouble(18),
            BaselineInputPricePerMillion: reader.IsDBNull(19) ? null : (decimal)reader.GetDouble(19),
            BaselineOutputPricePerMillion: reader.IsDBNull(20) ? null : (decimal)reader.GetDouble(20));
    }
}