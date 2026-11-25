using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.Transcripts;

public sealed partial class SqliteTranscriptStore
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<SessionTurnMetadata>> ListSessionTurnMetadataAsync(
        string sessionId, int limit, long? beforeTranscriptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.CurrentValue.Enabled) return Task.FromResult<IReadOnlyList<SessionTurnMetadata>>([]);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  t.id,
                                  substr(t.correlation_id, length(t.session_id) + 2, 10),
                                  t.created_at_utc,
                                  substr(t.requested_model, 1, 1025),
                                  substr(t.routed_model, 1, 1025),
                                  substr(t.dimension, 1, 1025),
                                  substr(t.difficulty, 1, 1025),
                                  substr(t.language, 1, 1025),
                                  t.is_utility,
                                  t.score,
                                  t.cost,
                                  t.is_exploratory,
                                  t.propensity,
                                  t.input_tokens,
                                  t.output_tokens,
                                  t.memory_entry_id,
                                  substr(t.dim_best_model, 1, 1025),
                                  substr(t.scorer_version, 1, 1025),
                                  t.is_judge_scored,
                                  substr(t.untrained_baseline_model, 1, 1025),
                                  t.untrained_baseline_predicted_score,
                                  t.prompt_text_length,
                                  t.response_text_length,
                                  t.has_tool_calls,
                                  c.compared_at_utc,
                                  c.observed_score,
                                  c.dimension_predicted_score,
                                  c.cluster_predicted_score,
                                  c.dimension_abs_error,
                                  c.cluster_abs_error,
                                  c.is_clustered,
                                  substr(c.baseline_model, 1, 1025),
                                  c.actual_cost_usd,
                                  c.baseline_estimated_cost_usd,
                                  c.estimated_net_savings_usd,
                                  c.baseline_predicted_score,
                                  c.estimated_regret,
                                  c.baseline_tokenizer_ratio,
                                  c.baseline_tokenizer_ratio_measured,
                                  c.baseline_input_tokens,
                                  c.baseline_output_tokens,
                                  c.baseline_input_price_per_million,
                                  c.baseline_output_price_per_million
                              FROM request_transcripts t
                              LEFT JOIN taxonomy_comparisons c ON c.transcript_id = t.id
                              WHERE t.session_id = $session
                                AND ($before IS NULL OR t.id < $before)
                              ORDER BY t.id DESC
                              LIMIT $limit;
                              """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$before", (object?)beforeTranscriptId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<SessionTurnMetadata>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadMetadata(reader));
        return Task.FromResult<IReadOnlyList<SessionTurnMetadata>>(rows);
    }

    /// <inheritdoc/>
    public Task<SessionAggregate?> GetSessionAggregateAsync(
        string sessionId, int liveFloorTurnNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.CurrentValue.Enabled) return Task.FromResult<SessionAggregate?>(null);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var totals = connection.CreateCommand();
        totals.Transaction = transaction;
        totals.CommandText = """
                             SELECT
                                 COUNT(*),
                                 COUNT(memory_entry_id),
                                 MIN(created_at_utc),
                                 MAX(created_at_utc),
                                 SUM(cost),
                                 SUM(CASE WHEN cost IS NULL THEN 1 ELSE 0 END),
                                 SUM(CASE WHEN input_tokens IS NOT NULL THEN input_tokens ELSE 0 END),
                                 SUM(CASE WHEN output_tokens IS NOT NULL THEN output_tokens ELSE 0 END),
                                 SUM(CASE WHEN input_tokens IS NULL OR output_tokens IS NULL THEN 1 ELSE 0 END)
                             FROM request_transcripts
                             WHERE session_id = $session;
                             """;
        totals.Parameters.AddWithValue("$session", sessionId);
        int persisted;
        int trained;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        decimal? cost = null;
        int unpriced;
        long inputTokens;
        long outputTokens;
        int tokenUnknown;
        using (var reader = totals.ExecuteReader())
        {
            reader.Read();
            persisted = reader.GetInt32(0);
            trained = reader.GetInt32(1);
            if (!reader.IsDBNull(2)) first = ParseTime(reader.GetString(2));
            if (!reader.IsDBNull(3)) last = ParseTime(reader.GetString(3));
            if (!reader.IsDBNull(4)) cost = StoredMoney.FromReal(reader.GetDouble(4));
            unpriced = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            inputTokens = reader.IsDBNull(6) ? 0 : Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture);
            outputTokens = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture);
            tokenUnknown = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
        }

        decimal? savings = null;
        decimal? baseline = null;
        int compared;
        using (var compare = connection.CreateCommand())
        {
            compare.Transaction = transaction;
            compare.CommandText = """
                                  SELECT COUNT(*), SUM(c.estimated_net_savings_usd), SUM(c.baseline_estimated_cost_usd)
                                  FROM request_transcripts t
                                  JOIN taxonomy_comparisons c ON c.transcript_id = t.id
                                  WHERE t.session_id = $session
                                    AND c.estimated_net_savings_usd IS NOT NULL
                                    AND c.baseline_estimated_cost_usd IS NOT NULL
                                    AND c.baseline_estimated_cost_usd > 0;
                                  """;
            compare.Parameters.AddWithValue("$session", sessionId);
            using var reader = compare.ExecuteReader();
            reader.Read();
            compared = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            if (!reader.IsDBNull(1)) savings = StoredMoney.FromReal(reader.GetDouble(1));
            if (!reader.IsDBNull(2)) baseline = StoredMoney.FromReal(reader.GetDouble(2));
        }

        var numbers = new List<int>();
        var numbersComplete = true;
        if (liveFloorTurnNumber > 0)
        {
            using var floors = connection.CreateCommand();
            floors.Transaction = transaction;
            floors.CommandText = """
                                 SELECT DISTINCT substr(correlation_id, length(session_id) + 2, 10)
                                 FROM request_transcripts
                                 WHERE session_id = $session;
                                 """;
            floors.Parameters.AddWithValue("$session", sessionId);
            using var reader = floors.ExecuteReader();
            while (reader.Read())
            {
                var turn = ParseTurn(reader.GetString(0));
                if (turn >= liveFloorTurnNumber) numbers.Add(turn);
            }

            numbers.Sort();
            if (numbers.Count > 2000)
            {
                numbers.RemoveAt(numbers.Count - 1);
                numbersComplete = false;
            }
        }

        var models = new List<SessionModelUsage>();
        var otherModels = 0;
        var otherTurns = 0;
        using (var modelCommand = connection.CreateCommand())
        {
            modelCommand.Transaction = transaction;
            modelCommand.CommandText = """
                                       SELECT substr(routed_model, 1, 1025), COUNT(*), SUM(cost),
                                              SUM(CASE WHEN cost IS NULL THEN 1 ELSE 0 END), MAX(created_at_utc)
                                       FROM request_transcripts
                                       WHERE session_id = $session
                                       GROUP BY routed_model
                                       ORDER BY MAX(created_at_utc) DESC;
                                       """;
            modelCommand.Parameters.AddWithValue("$session", sessionId);
            using var reader = modelCommand.ExecuteReader();
            while (reader.Read())
            {
                var usage = new SessionModelUsage(
                    Cap(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : StoredMoney.FromReal(reader.GetDouble(2)),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    ParseTime(reader.GetString(4)));
                if (models.Count < 20) models.Add(usage);
                else
                {
                    otherModels++;
                    otherTurns += usage.Turns;
                }
            }
        }

        transaction.Commit();
        return Task.FromResult<SessionAggregate?>(new SessionAggregate(
            persisted, trained, compared, savings, baseline, first, last, numbers, numbersComplete,
            cost, unpriced, inputTokens, outputTokens, tokenUnknown, models, otherModels, otherTurns));
    }

    private static SessionTurnMetadata ReadMetadata(SqliteDataReader reader)
    {
        SessionBaselineComparison? comparison = reader.IsDBNull(24)
            ? null
            : new SessionBaselineComparison(
                ParseTime(reader.GetString(24)),
                reader.GetDouble(25),
                OptionalDouble(reader, 26),
                OptionalDouble(reader, 27),
                OptionalDouble(reader, 28),
                OptionalDouble(reader, 29),
                reader.GetInt32(30) != 0,
                OptionalText(reader, 31),
                OptionalMoney(reader, 32),
                OptionalMoney(reader, 33),
                OptionalMoney(reader, 34),
                OptionalDouble(reader, 35),
                OptionalDouble(reader, 36),
                OptionalDouble(reader, 37),
                reader.IsDBNull(38) ? null : reader.GetInt32(38) != 0,
                OptionalDouble(reader, 39),
                OptionalDouble(reader, 40),
                OptionalMoney(reader, 41),
                OptionalMoney(reader, 42));

        return new SessionTurnMetadata(
            reader.GetInt64(0),
            ParseTurn(reader.IsDBNull(1) ? "" : reader.GetString(1)),
            ParseTime(reader.GetString(2)),
            Cap(reader.GetString(3)),
            Cap(reader.GetString(4)),
            OptionalText(reader, 5),
            OptionalText(reader, 6),
            OptionalText(reader, 7),
            reader.GetInt32(8) != 0,
            OptionalDouble(reader, 9),
            OptionalMoney(reader, 10),
            reader.GetInt32(11) != 0,
            reader.GetDouble(12),
            reader.IsDBNull(13) ? null : reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15),
            OptionalText(reader, 16),
            OptionalText(reader, 17),
            !reader.IsDBNull(18) && reader.GetInt32(18) != 0,
            OptionalText(reader, 19),
            OptionalDouble(reader, 20),
            reader.IsDBNull(21) ? null : reader.GetInt32(21),
            reader.IsDBNull(22) ? null : reader.GetInt32(22),
            reader.IsDBNull(23) ? null : reader.GetInt32(23) != 0,
            comparison);
    }

    private static string? OptionalText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Cap(reader.GetString(ordinal));

    private static double? OptionalDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static decimal? OptionalMoney(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : StoredMoney.FromReal(reader.GetDouble(ordinal));

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static int ParseTurn(string suffix) =>
        int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turn) && turn > 0 ? turn : 1;

    private static string Cap(string value) =>
        value.Length <= 1024 ? value : string.Concat(value.AsSpan(0, 1024), "…");
}
