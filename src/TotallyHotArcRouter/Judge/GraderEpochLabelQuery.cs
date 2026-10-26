using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The eligibility join no store in the tree returns. It reads <see cref="RouterMemoryDatabase"/> and
/// keeps a receipt only when it is a complete, undisputed teacher label for one kind: one judge score,
/// the current teacher model and prompt, and a response vector that opens under the vector key. A failed
/// open excludes the label and does not delete the row. <see cref="SqliteResponseVectorStore.TryOpen"/>
/// is the live path, and that method still deletes a blob that fails authentication.
/// </summary>
public sealed class GraderEpochLabelQuery
{
    /// <summary>
    /// The join. Tool-count names are not selected: they are not <c>grade_receipts</c> columns, and a
    /// reader that asked for them would be outside schema version 1.
    /// </summary>
    internal const string CommandText = """
                                        SELECT
                                            r.correlation_id,
                                            r.created_at_utc,
                                            r.prompt_version,
                                            r.judge_model,
                                            r.weights_version,
                                            r.question_vector_missing,
                                            r.session_id,
                                            r.response_has_code,
                                            r.response_length_chars,
                                            r.syntax_score,
                                            r.grade_status,
                                            r.incomplete_reason,
                                            v.embedding_model,
                                            v.vector,
                                            (SELECT COUNT(*) FROM grader_scores g
                                                WHERE g.correlation_id = r.correlation_id AND g.grader_key = $judgeKey),
                                            (SELECT g.score FROM grader_scores g
                                                WHERE g.correlation_id = r.correlation_id AND g.grader_key = $judgeKey
                                                LIMIT 1),
                                            (SELECT COUNT(*) FROM grader_scores g
                                                WHERE g.correlation_id = r.correlation_id AND g.grader_key = $analysisKey),
                                            (SELECT g.score FROM grader_scores g
                                                WHERE g.correlation_id = r.correlation_id AND g.grader_key = $analysisKey
                                                LIMIT 1),
                                            m.embedding_model,
                                            m.embedding
                                        FROM grade_receipts r
                                        LEFT JOIN response_vectors v ON v.correlation_id = r.correlation_id
                                        LEFT JOIN memory_entries m ON m.id = (
                                            SELECT m2.id FROM memory_entries m2
                                            WHERE m2.correlation_id = r.correlation_id
                                            ORDER BY CASE WHEN m2.embedding_model = $bge THEN 0 ELSE 1 END,
                                                     m2.created_at_utc DESC, m2.id DESC
                                            LIMIT 1)
                                        WHERE r.weights_version = $weights;
                                        """;

    private readonly RouterMemoryDatabase _database;
    private readonly IOptionsMonitor<EmbeddingOptions> _embedding;
    private readonly IOptionsMonitor<JudgeOptions> _judge;
    private readonly ResponseVectorKeyHolder _keys;
    private readonly IOptionsMonitor<QualityOptions> _quality;

    /// <summary>Initializes a new instance of the <see cref="GraderEpochLabelQuery"/> class.</summary>
    /// <param name="database">The router-memory database. Its schema is created if this is the first reader.</param>
    /// <param name="keys">The response-vector key. A failed open does not delete the row.</param>
    /// <param name="judge">The current teacher model and prompt version.</param>
    /// <param name="quality">The current code <c>weights_version</c>.</param>
    /// <param name="embedding">The current BGE identity and the only legal vector length.</param>
    public GraderEpochLabelQuery(
        RouterMemoryDatabase database,
        ResponseVectorKeyHolder keys,
        IOptionsMonitor<JudgeOptions> judge,
        IOptionsMonitor<QualityOptions> quality,
        IOptionsMonitor<EmbeddingOptions> embedding)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(embedding);
        _database = database;
        _keys = keys;
        _judge = judge;
        _quality = quality;
        _embedding = embedding;
    }

    /// <summary>
    /// Loads the eligible teacher labels for one kind. A miss is not a label and is not stored as 0.
    /// </summary>
    /// <param name="kind"><see cref="GraderEpochKinds.Code"/> or <see cref="GraderEpochKinds.NonCode"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The eligible labels. Order is not significant; the fit sorts them.</returns>
    internal Task<IReadOnlyList<GraderEpochLabel>> LoadForKindAsync(string kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind is not (GraderEpochKinds.Code or GraderEpochKinds.NonCode))
            return Task.FromResult<IReadOnlyList<GraderEpochLabel>>([]);

        var judge = _judge.CurrentValue;
        var embedding = _embedding.CurrentValue;
        var bge = embedding.ResolveModelIdentity();
        var dimension = embedding.EmbeddingDimension;
        var weights = GraderEpochKinds.WeightsForKind(kind: kind, codeWeightsVersion: _quality.CurrentValue.WeightsVersion);
        var key = _keys.GetOrCreate();

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = CommandText;
        command.Parameters.AddWithValue("$judgeKey", GraderKeys.Judge);
        command.Parameters.AddWithValue("$analysisKey", GraderKeys.Analysis);
        command.Parameters.AddWithValue("$bge", bge);
        command.Parameters.AddWithValue("$weights", weights);

        var labels = new List<GraderEpochLabel>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = TryReadLabel(reader: reader, judge: judge, bge: bge, dimension: dimension, key: key);
            if (label is not null) labels.Add(label);
        }

        return Task.FromResult<IReadOnlyList<GraderEpochLabel>>(labels);
    }

    /// <summary>
    /// The question vector the shadow stamp reads from <c>memory_entries</c>. The pending cache has
    /// already been taken; this does not re-embed the question.
    /// </summary>
    /// <param name="correlationId">The grade's correlation id.</param>
    /// <param name="modelId">The chosen memory row's model id, or null.</param>
    /// <param name="vector">The decoded vector, or null when the row or the blob is absent.</param>
    internal void TryReadQuestion(string correlationId, out string? modelId, out float[]? vector)
    {
        modelId = null;
        vector = null;
        if (string.IsNullOrWhiteSpace(correlationId)) return;

        var bge = _embedding.CurrentValue.ResolveModelIdentity();
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT embedding_model, embedding FROM memory_entries
                              WHERE correlation_id = $correlationId
                              ORDER BY CASE WHEN embedding_model = $bge THEN 0 ELSE 1 END,
                                       created_at_utc DESC, id DESC
                              LIMIT 1;
                              """;
        command.Parameters.AddWithValue("$correlationId", correlationId);
        command.Parameters.AddWithValue("$bge", bge);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return;

        modelId = reader.IsDBNull(0) ? null : reader.GetString(0);
        vector = DecodeFloats(ReadBlob(reader, 1));
    }

    private static GraderEpochLabel? TryReadLabel(SqliteDataReader reader, JudgeOptions judge, string bge,
        int dimension, byte[] key)
    {
        if (!string.Equals(reader.GetString(10), nameof(GradeStatus.Complete), StringComparison.Ordinal))
            return null;
        if (!reader.IsDBNull(11)) return null;

        var prompt = reader.IsDBNull(2) ? null : reader.GetString(2);
        var model = reader.IsDBNull(3) ? null : reader.GetString(3);
        if (!string.Equals(prompt, judge.PromptVersion, StringComparison.Ordinal)) return null;
        if (!string.Equals(model, judge.ModelName, StringComparison.Ordinal)) return null;

        var judgeCount = reader.GetInt64(14);
        if (judgeCount != 1 || reader.IsDBNull(15)) return null;

        var responseModel = reader.IsDBNull(12) ? null : reader.GetString(12);
        var responseBlob = ReadBlob(reader, 13);
        if (responseBlob is null || responseModel is null ||
            !string.Equals(responseModel, bge, StringComparison.Ordinal))
            return null;

        var correlationId = reader.GetString(0);
        if (!ResponseVectorSeal.TryOpen(key: key, correlationId: correlationId, modelId: responseModel,
                blob: responseBlob, vector: out var responseVector))
            return null;
        if (!GraderEpochVectors.IsUsable(responseVector, dimension)) return null;

        var createdRaw = reader.GetString(1);
        DateTimeOffset? created = DateTimeOffset.TryParse(createdRaw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        var session = reader.IsDBNull(6) ? null : reader.GetString(6);
        if (string.IsNullOrEmpty(session)) session = null;

        return new GraderEpochLabel
        {
            CorrelationId = correlationId,
            CreatedAtUtcRaw = createdRaw,
            CreatedAt = created,
            SessionId = session,
            QuestionVectorMissing = reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0,
            QuestionVector = DecodeFloats(ReadBlob(reader, 19)),
            QuestionModel = reader.IsDBNull(18) ? null : reader.GetString(18),
            AnalysisCount = (int)reader.GetInt64(16),
            AnalysisScore = reader.IsDBNull(17) ? null : reader.GetDouble(17),
            SyntaxScore = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            ResponseHasCode = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
            ResponseLengthChars = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ResponseVector = responseVector,
            JudgeScore = reader.GetDouble(15)
        };
    }

    private static byte[]? ReadBlob(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);

    private static float[]? DecodeFloats(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % sizeof(float) != 0) return null;

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(src: bytes, srcOffset: 0, dst: vector, dstOffset: 0, count: bytes.Length);
        return vector;
    }
}
