using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> LoadPendingComparisonsAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

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
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return Task.FromResult<IReadOnlyList<long>>(ids);
    }

    /// <inheritdoc />
    public Task UpsertAsync(TaxonomyComparisonRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO taxonomy_comparisons (
                transcript_id, compared_at_utc, session_id, observed_score, dimension_predicted_score,
                cluster_predicted_score, dimension_abs_error, cluster_abs_error, is_clustered,
                is_exploratory, routed_model, baseline_model, actual_cost_usd,
                baseline_estimated_cost_usd, estimated_net_savings_usd,
                baseline_predicted_score, estimated_regret)
            VALUES (
                $transcriptId, $comparedAtUtc, $sessionId, $observedScore, $dimensionPredicted,
                $clusterPredicted, $dimensionError, $clusterError, $isClustered,
                $isExploratory, $routedModel, $baselineModel, $actualCost,
                $baselineCost, $netSavings, $baselinePredicted, $estimatedRegret)
            ON CONFLICT(transcript_id) DO UPDATE SET
                compared_at_utc = excluded.compared_at_utc,
                session_id = excluded.session_id,
                observed_score = excluded.observed_score,
                dimension_predicted_score = excluded.dimension_predicted_score,
                cluster_predicted_score = excluded.cluster_predicted_score,
                dimension_abs_error = excluded.dimension_abs_error,
                cluster_abs_error = excluded.cluster_abs_error,
                is_clustered = excluded.is_clustered,
                is_exploratory = excluded.is_exploratory,
                routed_model = excluded.routed_model,
                baseline_model = excluded.baseline_model,
                actual_cost_usd = excluded.actual_cost_usd,
                baseline_estimated_cost_usd = excluded.baseline_estimated_cost_usd,
                estimated_net_savings_usd = excluded.estimated_net_savings_usd,
                baseline_predicted_score = excluded.baseline_predicted_score,
                estimated_regret = excluded.estimated_regret;
            """;
        command.Parameters.AddWithValue("$transcriptId", record.TranscriptId);
        command.Parameters.AddWithValue("$comparedAtUtc", record.ComparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sessionId", record.SessionId);
        command.Parameters.AddWithValue("$observedScore", record.ObservedScore);
        command.Parameters.AddWithValue("$dimensionPredicted", (object?)record.DimensionPredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$clusterPredicted", (object?)record.ClusterPredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$dimensionError", (object?)record.DimensionAbsoluteError ?? DBNull.Value);
        command.Parameters.AddWithValue("$clusterError", (object?)record.ClusterAbsoluteError ?? DBNull.Value);
        command.Parameters.AddWithValue("$isClustered", record.IsClustered ? 1 : 0);
        command.Parameters.AddWithValue("$isExploratory", record.IsExploratory ? 1 : 0);
        command.Parameters.AddWithValue("$routedModel", record.RoutedModel);
        command.Parameters.AddWithValue("$baselineModel", (object?)record.BaselineModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$actualCost", record.ActualCostUsd is { } actual ? (double)actual : DBNull.Value);
        command.Parameters.AddWithValue("$baselineCost", record.BaselineEstimatedCostUsd is { } baseline ? (double)baseline : DBNull.Value);
        command.Parameters.AddWithValue("$netSavings", record.EstimatedNetSavingsUsd is { } savings ? (double)savings : DBNull.Value);
        command.Parameters.AddWithValue("$baselinePredicted", (object?)record.BaselinePredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$estimatedRegret", (object?)record.EstimatedRegret ?? DBNull.Value);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaxonomyComparisonRecord>> LoadSinceAsync(
        DateTimeOffset since,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.FromResult<IReadOnlyList<TaxonomyComparisonRecord>>([]);
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                transcript_id, compared_at_utc, session_id, observed_score, dimension_predicted_score,
                cluster_predicted_score, dimension_abs_error, cluster_abs_error, is_clustered,
                is_exploratory, routed_model, baseline_model, actual_cost_usd,
                baseline_estimated_cost_usd, estimated_net_savings_usd,
                baseline_predicted_score, estimated_regret
            FROM taxonomy_comparisons
            WHERE compared_at_utc >= $since AND ($sessionId IS NULL OR session_id = $sessionId)
            ORDER BY compared_at_utc ASC, transcript_id ASC;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);

        var rows = new List<TaxonomyComparisonRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return Task.FromResult<IReadOnlyList<TaxonomyComparisonRecord>>(rows);
    }

    /// <summary>Materializes one row into a <see cref="TaxonomyComparisonRecord"/>.</summary>
    /// <param name="reader">A reader positioned on a row selecting this table's columns in declaration order.</param>
    /// <returns>The materialized record.</returns>
    private static TaxonomyComparisonRecord Read(SqliteDataReader reader) =>
        new(
            TranscriptId: reader.GetInt64(0),
            ComparedAtUtc: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
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
            EstimatedRegret: reader.IsDBNull(16) ? null : reader.GetDouble(16));
}
