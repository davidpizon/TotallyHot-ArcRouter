using Microsoft.Data.Sqlite;
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

    /// <inheritdoc />
    public Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO judge_shadow_scores (
                correlation_id, created_at_utc, dimension, model, verifier_score, judge_score,
                judge_model, judge_prompt_version, judge_latency_ms, used_logprobs, executed)
            VALUES (
                $correlationId, $createdAtUtc, $dimension, $model, $verifierScore, $judgeScore,
                $judgeModel, $judgePromptVersion, $judgeLatencyMs, $usedLogprobs, $executed);
            """;
        command.Parameters.AddWithValue("$correlationId", record.CorrelationId);
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$dimension", record.Dimension);
        command.Parameters.AddWithValue("$model", record.Model);
        command.Parameters.AddWithValue("$verifierScore", record.VerifierScore);
        command.Parameters.AddWithValue("$judgeScore", record.JudgeScore);
        command.Parameters.AddWithValue("$judgeModel", record.JudgeModel);
        command.Parameters.AddWithValue("$judgePromptVersion", record.JudgePromptVersion);
        command.Parameters.AddWithValue("$judgeLatencyMs", record.JudgeLatencyMs);
        command.Parameters.AddWithValue("$usedLogprobs", record.UsedLogprobs ? 1 : 0);
        command.Parameters.AddWithValue("$executed", record.Executed ? 1 : 0);

        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM judge_shadow_scores;";
        return Task.FromResult(Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (count <= 0)
        {
            return Task.FromResult(0);
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM judge_shadow_scores
            WHERE id IN (SELECT id FROM judge_shadow_scores ORDER BY id ASC LIMIT $count);
            """;
        command.Parameters.AddWithValue("$count", count);
        return Task.FromResult(command.ExecuteNonQuery());
    }

    /// <inheritdoc />
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM judge_shadow_scores WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return Task.FromResult(command.ExecuteNonQuery());
    }
}
