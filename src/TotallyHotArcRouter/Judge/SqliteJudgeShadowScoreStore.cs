using System.Globalization;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A SQLite-backed <see cref="IJudgeShadowScoreStore"/> over <see cref="RouterMemoryDatabase"/>'s
/// <c>judge_shadow_scores</c> table (docs/router/geval-shadow-scoring-plan.md §1d). Shares the
/// router-memory database file rather than a dedicated one - unlike <c>TranscriptDatabase</c>, this table
/// carries no raw text, so it needs none of that file's opt-in-creation treatment.
/// </summary>
public sealed class SqliteJudgeShadowScoreStore : IJudgeShadowScoreStore
{
    private readonly RouterMemoryDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="SqliteJudgeShadowScoreStore"/> class.</summary>
    /// <param name="database">The router-memory database to persist rows in. Its schema must already be created.</param>
    public SqliteJudgeShadowScoreStore(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO judge_shadow_scores (
                                  correlation_id, created_at_utc, dimension, model, static_score, judge_score,
                                  judge_model, judge_prompt_version, judge_latency_ms, used_logprobs,
                                  syntax_authoritative)
                              VALUES (
                                  $correlationId, $createdAtUtc, $dimension, $model, $staticScore, $judgeScore,
                                  $judgeModel, $judgePromptVersion, $judgeLatencyMs, $usedLogprobs,
                                  $syntaxAuthoritative);
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: record.CorrelationId);
        command.Parameters.AddWithValue(parameterName: "$createdAtUtc", value: record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(parameterName: "$dimension", value: record.Dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: record.Model);
        command.Parameters.AddWithValue(parameterName: "$staticScore", value: record.StaticScore);
        command.Parameters.AddWithValue(parameterName: "$judgeScore", value: record.JudgeScore);
        command.Parameters.AddWithValue(parameterName: "$judgeModel", value: record.JudgeModel);
        command.Parameters.AddWithValue(parameterName: "$judgePromptVersion", value: record.JudgePromptVersion);
        command.Parameters.AddWithValue(parameterName: "$judgeLatencyMs", value: record.JudgeLatencyMs);
        command.Parameters.AddWithValue(parameterName: "$usedLogprobs", value: record.UsedLogprobs ? 1 : 0);

        // DBNull, not a substituted 0/1: the live path always supplies a value, so a null here can only
        // come from a caller that genuinely has no verdict, and writing one of the two real answers
        // instead would make the row claim an authority nothing established.
        command.Parameters.AddWithValue(parameterName: "$syntaxAuthoritative",
            value: record.SyntaxAuthoritative is { } authoritative ? authoritative ? 1 : 0 : DBNull.Value);

        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM judge_shadow_scores;";
        return Task.FromResult(Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<JudgeShadowScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, correlation_id, created_at_utc, dimension, model, static_score,
                                     judge_score, judge_model, judge_prompt_version, judge_latency_ms,
                                     used_logprobs, syntax_authoritative
                              FROM judge_shadow_scores
                              ORDER BY id ASC;
                              """;

        List<JudgeShadowScoreRecord> records = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(new JudgeShadowScoreRecord(
                reader.GetInt64(0),
                CorrelationId: reader.GetString(1),
                CreatedAtUtc: DateTimeOffset.Parse(input: reader.GetString(2), formatProvider: CultureInfo.InvariantCulture,
                    styles: DateTimeStyles.RoundtripKind),
                Dimension: reader.GetString(3),
                Model: reader.GetString(4),
                StaticScore: reader.GetDouble(5),
                JudgeScore: reader.GetDouble(6),
                JudgeModel: reader.GetString(7),
                JudgePromptVersion: reader.GetString(8),
                JudgeLatencyMs: reader.GetInt64(9),
                UsedLogprobs: reader.GetInt64(10) != 0,
                SyntaxAuthoritative: reader.IsDBNull(11) ? null : reader.GetInt64(11) != 0));

        return Task.FromResult<IReadOnlyList<JudgeShadowScoreRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (count <= 0) return Task.FromResult(0);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM judge_shadow_scores
                              WHERE id IN (SELECT id FROM judge_shadow_scores ORDER BY id ASC LIMIT $count);
                              """;
        command.Parameters.AddWithValue(parameterName: "$count", value: count);
        return Task.FromResult(command.ExecuteNonQuery());
    }

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM judge_shadow_scores WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff.ToString("O"));
        return Task.FromResult(command.ExecuteNonQuery());
    }
}