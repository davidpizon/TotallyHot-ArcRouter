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
                                  fingerprint_ref, scorer_version, prompt_version, weights_version, abstained_graders,
                                  incomplete_reason, grade_source, response_has_code, judge_model, answer_model,
                                  question_vector_missing)
                              VALUES (
                                  $correlationId, $createdAtUtc, $dimension, $model, $gradeStatus, $unifiedScore,
                                  $questionHmac, $questionChars, $contextMessages, $questionTruncated,
                                  $fingerprintRef, $scorerVersion, $promptVersion, $weightsVersion, $abstainedGraders,
                                  $incompleteReason, $gradeSource, $responseHasCode, $judgeModel, $answerModel,
                                  $questionVectorMissing)
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
                                  abstained_graders = excluded.abstained_graders,
                                  incomplete_reason = excluded.incomplete_reason,
                                  grade_source = excluded.grade_source,
                                  response_has_code = excluded.response_has_code,
                                  judge_model = excluded.judge_model,
                                  answer_model = excluded.answer_model,
                                  question_vector_missing = excluded.question_vector_missing;
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
                                     fingerprint_ref, scorer_version, prompt_version, weights_version, abstained_graders,
                                     incomplete_reason, grade_source, response_has_code, judge_model, answer_model,
                                     question_vector_missing
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
        command.Parameters.AddWithValue(parameterName: "$incompleteReason",
            value: (object?)record.IncompleteReason ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$gradeSource",
            value: (object?)record.GradeSource ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$responseHasCode", value: record.ResponseHasCode ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$judgeModel",
            value: (object?)record.JudgeModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$answerModel",
            value: (object?)record.AnswerModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$questionVectorMissing",
            value: record.QuestionVectorMissing switch
            {
                true => 1,
                false => 0,
                _ => DBNull.Value
            });
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
            AbstainedGraders: reader.GetString(14),
            IncompleteReason: reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null,
            GradeSource: reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : null,
            ResponseHasCode: reader.FieldCount > 17 && !reader.IsDBNull(17) && reader.GetInt64(17) != 0,
            JudgeModel: reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetString(18) : null,
            AnswerModel: reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
            QuestionVectorMissing: reader.FieldCount > 20 && !reader.IsDBNull(20)
                ? reader.GetInt64(20) != 0
                : null);
    }
}
