using System.Globalization;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// SQLite-backed <see cref="IGradeReceiptStore"/> over <c>grade_receipts</c> in
/// <see cref="RouterMemoryDatabase"/> (issue #293 plan §2.4).
/// </summary>
public sealed class SqliteGradeReceiptStore : IGradeReceiptStore
{
    private readonly RouterMemoryDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="SqliteGradeReceiptStore"/> class.</summary>
    /// <param name="database">The router-memory database whose schema must already be created.</param>
    public SqliteGradeReceiptStore(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public Task UpsertAsync(GradeReceiptRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO grade_receipts (
                                  correlation_id, created_at_utc, dimension, model, grade_status, unified_score,
                                  question_hmac, question_chars, context_messages, question_truncated,
                                  fingerprint_ref, scorer_version, prompt_version, weights_version, abstained_graders)
                              VALUES (
                                  $correlationId, $createdAtUtc, $dimension, $model, $gradeStatus, $unifiedScore,
                                  $questionHmac, $questionChars, $contextMessages, $questionTruncated,
                                  $fingerprintRef, $scorerVersion, $promptVersion, $weightsVersion, $abstainedGraders)
                              ON CONFLICT(correlation_id) DO UPDATE SET
                                  created_at_utc = excluded.created_at_utc,
                                  dimension = excluded.dimension,
                                  model = excluded.model,
                                  grade_status = excluded.grade_status,
                                  unified_score = excluded.unified_score,
                                  question_hmac = excluded.question_hmac,
                                  question_chars = excluded.question_chars,
                                  context_messages = excluded.context_messages,
                                  question_truncated = excluded.question_truncated,
                                  fingerprint_ref = excluded.fingerprint_ref,
                                  scorer_version = excluded.scorer_version,
                                  prompt_version = excluded.prompt_version,
                                  weights_version = excluded.weights_version,
                                  abstained_graders = excluded.abstained_graders;
                              """;
        BindRecord(command, record);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<GradeReceiptRecord?> TryGetAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT correlation_id, created_at_utc, dimension, model, grade_status, unified_score,
                                     question_hmac, question_chars, context_messages, question_truncated,
                                     fingerprint_ref, scorer_version, prompt_version, weights_version, abstained_graders
                              FROM grade_receipts
                              WHERE correlation_id = $correlationId;
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<GradeReceiptRecord?>(null);

        return Task.FromResult<GradeReceiptRecord?>(ReadRecord(reader));
    }

    /// <inheritdoc/>
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM grade_receipts;";
        return Task.FromResult(Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture));
    }

    /// <inheritdoc/>
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= 0) return Task.FromResult(0);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM grade_receipts
                              WHERE correlation_id IN (
                                  SELECT correlation_id FROM grade_receipts
                                  ORDER BY created_at_utc ASC LIMIT $count);
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
        command.CommandText = "DELETE FROM grade_receipts WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff.ToString("O"));
        return Task.FromResult(command.ExecuteNonQuery());
    }

    private static void BindRecord(Microsoft.Data.Sqlite.SqliteCommand command, GradeReceiptRecord record)
    {
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: record.CorrelationId);
        command.Parameters.AddWithValue(parameterName: "$createdAtUtc", value: record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(parameterName: "$dimension", value: record.Dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: record.Model);
        command.Parameters.AddWithValue(parameterName: "$gradeStatus", value: record.GradeStatus.ToString());
        command.Parameters.AddWithValue(parameterName: "$unifiedScore", value: record.UnifiedScore);
        command.Parameters.AddWithValue(parameterName: "$questionHmac",
            value: (object?)record.QuestionHmac ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$questionChars",
            value: (object?)record.QuestionChars ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$contextMessages",
            value: (object?)record.ContextMessages ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$questionTruncated", value: record.QuestionTruncated ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$fingerprintRef",
            value: (object?)record.FingerprintRef ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$scorerVersion", value: record.ScorerVersion);
        command.Parameters.AddWithValue(parameterName: "$promptVersion",
            value: (object?)record.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$weightsVersion", value: record.WeightsVersion);
        command.Parameters.AddWithValue(parameterName: "$abstainedGraders", value: record.AbstainedGraders);
    }

    private static GradeReceiptRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new GradeReceiptRecord(
            CorrelationId: reader.GetString(0),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            Dimension: reader.GetString(2),
            Model: reader.GetString(3),
            GradeStatus: Enum.Parse<GradeStatus>(reader.GetString(4)),
            UnifiedScore: reader.GetDouble(5),
            QuestionHmac: reader.IsDBNull(6) ? null : reader.GetString(6),
            QuestionChars: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            ContextMessages: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            QuestionTruncated: reader.GetInt64(9) != 0,
            FingerprintRef: reader.IsDBNull(10) ? null : reader.GetString(10),
            ScorerVersion: reader.GetString(11),
            PromptVersion: reader.IsDBNull(12) ? null : reader.GetString(12),
            WeightsVersion: reader.GetString(13),
            AbstainedGraders: reader.GetString(14));
    }
}
