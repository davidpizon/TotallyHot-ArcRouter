using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Storage;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// A SQLite-backed <see cref="ITranscriptStore"/> over <see cref="TranscriptDatabase"/>'s
/// <c>request_transcripts</c> table. Every method reads <see cref="TranscriptOptions.Enabled"/> live off
/// <see cref="IOptionsMonitor{TOptions}"/> and no-ops when capture is currently disabled, so a caller never
/// needs its own enabled check and the System Settings window's Transcription Capture toggle takes effect
/// immediately - the same live-gate posture <see cref="Judge.GraderDispatcher"/> takes for
/// <see cref="Judge.JudgeOptions.Enabled"/>.
/// </summary>
public sealed class SqliteTranscriptStore : ITranscriptStore
{
    private readonly TranscriptDatabase _database;
    private readonly Lazy<ISessionExtractReader>? _extracts;
    private readonly ILogger<SqliteTranscriptStore> _logger;
    private readonly IOptionsMonitor<TranscriptOptions> _options;
    private readonly Lock _schemaLock = new();
    private volatile bool _schemaEnsured;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteTranscriptStore"/> class.
    /// </summary>
    /// <param name="database">
    /// The database to persist rows in. Its schema is created lazily, on first use, rather than only at
    /// startup - see <see cref="EnsureSchema"/>.
    /// </param>
    /// <param name="options">
    /// Supplies the live <see cref="TranscriptOptions.Enabled"/> gate, read per call rather than
    /// captured.
    /// </param>
    /// <param name="logger">
    /// Reports a log truncation that could not complete after a delete. Optional so a store built by hand
    /// in a test needs no logging setup.
    /// </param>
    /// <param name="extracts">
    /// Reads prompt and response text from session files. Optional so a store built by hand in a test
    /// returns rows whose text is null.
    /// </param>
    public SqliteTranscriptStore(
        TranscriptDatabase database,
        IOptionsMonitor<TranscriptOptions> options,
        ILogger<SqliteTranscriptStore>? logger = null,
        Lazy<ISessionExtractReader>? extracts = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options;
        _logger = logger ?? NullLogger<SqliteTranscriptStore>.Instance;
        _extracts = extracts;
    }

    /// <summary>
    /// Gets or sets the pause between the attempts <see cref="FinalizeDeletionAsync"/> makes while another
    /// connection holds the write-ahead log. Internal so a test does not wait out the real few seconds.
    /// </summary>
    internal TimeSpan FinalizeRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many times <see cref="FinalizeDeletionAsync"/> tries before reporting the deletion as not final.</summary>
    private const int FinalizeAttempts = 5;

    /// <inheritdoc/>
    public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult<long?>(null);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO request_transcripts (
                                  correlation_id, session_id, created_at_utc, requested_model, routed_model, dimension,
                                  difficulty, language, is_utility, prompt_text, response_text, score, cost, is_exploratory,
                                  propensity, input_tokens, output_tokens, memory_entry_id, dim_best_model,
                                  untrained_baseline_model, untrained_baseline_predicted_score,
                                  archive_session_id, archive_turn_id, prompt_text_length, response_text_length)
                              VALUES (
                                  $correlationId, $sessionId, $createdAtUtc, $requestedModel, $routedModel, $dimension,
                                  $difficulty, $language, $isUtility, NULL, NULL, $score, $cost,
                                  $isExploratory, $propensity, $inputTokens, $outputTokens, $memoryEntryId, $dimBestModel,
                                  $untrainedBaselineModel, $untrainedBaselinePredictedScore,
                                  $archiveSessionId, $archiveTurnId, $promptTextLength, $responseTextLength);
                              SELECT last_insert_rowid();
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: record.CorrelationId);
        command.Parameters.AddWithValue(parameterName: "$sessionId",
            value: CorrelationIdParser.SessionIdOf(record.CorrelationId));
        command.Parameters.AddWithValue(parameterName: "$createdAtUtc", value: record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(parameterName: "$requestedModel", value: record.RequestedModel);
        command.Parameters.AddWithValue(parameterName: "$routedModel", value: record.RoutedModel);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: (object?)record.Dimension ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$difficulty",
            value: (object?)record.Difficulty ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$language", value: (object?)record.Language ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$isUtility", value: record.IsUtility ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$score", value: (object?)record.Score ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$cost", value: (object?)record.Cost ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$isExploratory", value: record.IsExploratory ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$propensity", value: record.Propensity);
        command.Parameters.AddWithValue(parameterName: "$inputTokens",
            value: (object?)record.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$outputTokens",
            value: (object?)record.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$memoryEntryId",
            value: (object?)record.MemoryEntryId ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$dimBestModel",
            value: (object?)record.DimBestModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$untrainedBaselineModel",
            value: (object?)record.UntrainedBaselineModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$untrainedBaselinePredictedScore",
            value: (object?)record.UntrainedBaselinePredictedScore ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$archiveSessionId",
            value: record.ArchiveSessionId is { } sessionId ? sessionId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$archiveTurnId",
            value: record.ArchiveTurnId is { } turnId ? turnId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$promptTextLength", value: CharacterLength(record.PromptText));
        command.Parameters.AddWithValue(parameterName: "$responseTextLength", value: CharacterLength(record.ResponseText));

        var id = (long)command.ExecuteScalar()!;
        return Task.FromResult<long?>(id);
    }

    /// <inheritdoc/>
    public Task UpdateOutcomeAsync(string correlationId, double? score, bool isJudgeScored = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.CompletedTask;

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        // Updates every matching row, not just the newest - in the ordinary case there is exactly one
        // (correlation ids are per-request), and this avoids a second query to find "the" row when more
        // than one somehow shares an id.
        command.CommandText =
            "UPDATE request_transcripts SET score = $score, is_judge_scored = $isJudgeScored WHERE correlation_id = $correlationId;";
        command.Parameters.AddWithValue(parameterName: "$score", value: (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$isJudgeScored", value: isJudgeScored ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult<IReadOnlyList<long>>([]);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id FROM request_transcripts
                              WHERE memory_entry_id IS NULL AND score IS NOT NULL
                              ORDER BY id ASC
                              LIMIT $limit;
                              """;
        command.Parameters.AddWithValue(parameterName: "$limit", value: limit);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));

        return Task.FromResult<IReadOnlyList<long>>(ids);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(
        string scorerVersion,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scorerVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult<IReadOnlyList<long>>([]);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        // "IS NOT $scorerVersion" rather than "<> $scorerVersion": SQL's three-valued logic makes
        // `NULL <> 'x'` evaluate to NULL, not true, so a plain inequality would silently exclude every
        // never-scanned row - exactly the rows this sweep exists to find.
        command.CommandText = """
                              SELECT id FROM request_transcripts
                              WHERE response_text_length > 0
                                AND scorer_version IS NOT $scorerVersion
                              ORDER BY id ASC
                              LIMIT $limit;
                              """;
        command.Parameters.AddWithValue(parameterName: "$scorerVersion", value: scorerVersion);
        command.Parameters.AddWithValue(parameterName: "$limit", value: limit);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));

        return Task.FromResult<IReadOnlyList<long>>(ids);
    }

    /// <inheritdoc/>
    public Task MarkQualityRescannedAsync(
        long transcriptId,
        string scorerVersion,
        double? score,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transcriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scorerVersion);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.CompletedTask;

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE request_transcripts
                              SET score = $score, scorer_version = $scorerVersion
                              WHERE id = $id;
                              """;
        command.Parameters.AddWithValue(parameterName: "$score", value: (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$scorerVersion", value: scorerVersion);
        command.Parameters.AddWithValue(parameterName: "$id", value: transcriptId);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult<TranscriptRecord?>(null);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  id, correlation_id, created_at_utc, requested_model, routed_model, dimension, difficulty,
                                  language, is_utility, prompt_text, response_text, score, cost, is_exploratory, propensity,
                                  input_tokens, output_tokens, memory_entry_id, dim_best_model, untrained_baseline_model,
                                  untrained_baseline_predicted_score, is_judge_scored,
                                  archive_session_id, archive_turn_id
                              FROM request_transcripts
                              WHERE id = $id;
                              """;
        command.Parameters.AddWithValue(parameterName: "$id", value: id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<TranscriptRecord?>(null);

        return Task.FromResult<TranscriptRecord?>(WithExtracts(ReadTranscriptRecord(reader)));
    }

    /// <inheritdoc/>
    public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transcriptId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryEntryId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.CompletedTask;

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE request_transcripts SET memory_entry_id = $memoryEntryId WHERE id = $transcriptId;";
        command.Parameters.AddWithValue(parameterName: "$memoryEntryId", value: memoryEntryId);
        command.Parameters.AddWithValue(parameterName: "$transcriptId", value: transcriptId);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult(0);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM request_transcripts;";

        var count = (long)command.ExecuteScalar()!;
        return Task.FromResult((int)count);
    }

    /// <inheritdoc/>
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult(0);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM request_transcripts
                              WHERE id IN (SELECT id FROM request_transcripts ORDER BY id ASC LIMIT $count);
                              """;
        command.Parameters.AddWithValue(parameterName: "$count", value: count);

        var affectedRows = command.ExecuteNonQuery();
        TruncateWalAfterDelete(connection);
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled) return Task.FromResult(0);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM request_transcripts WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff.ToString("O"));

        var affectedRows = command.ExecuteNonQuery();
        TruncateWalAfterDelete(connection);
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc/>
    public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately not gated on _options.CurrentValue.Enabled, unlike every other method here - see
        // the interface doc. EnsureSchema() still makes this safe when capture has never run: the table
        // is created empty and the DELETE affects zero rows, rather than throwing "no such table".
        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM request_transcripts;";

        var affectedRows = command.ExecuteNonQuery();
        TruncateWalAfterDelete(connection);
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc/>
    public async Task<bool> FinalizeDeletionAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= FinalizeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using (var connection = _database.OpenConnection())
            {
                if (SqliteHardening.TruncateWal(connection)) return true;
            }

            if (attempt < FinalizeAttempts)
                await Task.Delay(delay: FinalizeRetryDelay, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }

        _logger.LogWarning("Transcript deletion is not final: another connection held the write-ahead log through every retry.");
        return false;
    }

    /// <summary>
    /// Truncates the write-ahead log right after a delete so the deleted text cannot survive in the
    /// <c>-wal</c> file (<c>secure_delete</c> only zeroes the freed pages in the database itself). A busy
    /// log is not an error: the next retention cycle, <see cref="FinalizeDeletionAsync"/>, or the startup
    /// checkpoint finishes the job, so it is logged and the delete still succeeds.
    /// </summary>
    /// <param name="connection">The connection that ran the delete.</param>
    private void TruncateWalAfterDelete(SqliteConnection connection)
    {
        if (!SqliteHardening.TruncateWal(connection))
            _logger.LogWarning("Transcript deletion is not yet final: the write-ahead log was busy and will be truncated on a later attempt.");
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
            return Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>());

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT memory_entry_id, archive_session_id, archive_turn_id
                              FROM request_transcripts
                              WHERE memory_entry_id IS NOT NULL
                                AND archive_session_id IS NOT NULL
                                AND archive_turn_id IS NOT NULL;
                              """;

        var links = new List<(long MemoryId, Guid SessionId, Guid TurnId)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(1), out var sessionId) &&
                    Guid.TryParse(reader.GetString(2), out var turnId))
                {
                    links.Add((reader.GetInt64(0), sessionId, turnId));
                }
            }
        }

        var promptTextByMemoryEntryId = new Dictionary<long, string>();
        foreach (var (memoryId, sessionId, turnId) in links)
        {
            if (ReadExtracts(sessionId, turnId)?.NewestUserMessage is { Length: > 0 } prompt)
                promptTextByMemoryEntryId[memoryId] = prompt;
        }

        return Task.FromResult<IReadOnlyDictionary<long, string>>(promptTextByMemoryEntryId);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SessionTranscript>> ListSessionsAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
            return Task.FromResult<IReadOnlyList<SessionTranscript>>([]);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  id, session_id, correlation_id, created_at_utc, requested_model, routed_model,
                                  cost, input_tokens, output_tokens, memory_entry_id,
                                  prompt_text_length, response_text_length
                              FROM request_transcripts
                              ORDER BY id DESC
                              LIMIT $limit;
                              """;
        command.Parameters.AddWithValue(parameterName: "$limit", value: limit);

        var rows = new List<SessionTranscript>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new SessionTranscript(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                CorrelationId: reader.GetString(2),
                CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(3)),
                RequestedModel: reader.GetString(4),
                RoutedModel: reader.GetString(5),
                PromptText: null,
                ResponseText: null,
                Cost: reader.IsDBNull(6) ? null : (decimal)reader.GetDouble(6),
                InputTokens: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                OutputTokens: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                MemoryEntryId: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                PromptTextLength: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                ResponseTextLength: reader.IsDBNull(11) ? null : reader.GetInt32(11)));

        return Task.FromResult<IReadOnlyList<SessionTranscript>>(rows);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
            return Task.FromResult<IReadOnlyDictionary<string, ModelTokenAverage>>(
                new Dictionary<string, ModelTokenAverage>(StringComparer.Ordinal));

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT routed_model, AVG(input_tokens), AVG(output_tokens), COUNT(*)
                              FROM request_transcripts
                              WHERE input_tokens IS NOT NULL AND output_tokens IS NOT NULL
                              GROUP BY routed_model;
                              """;

        var averages = new Dictionary<string, ModelTokenAverage>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            averages[reader.GetString(0)] = new ModelTokenAverage(
                InputTokens: reader.GetDouble(1), OutputTokens: reader.GetDouble(2),
                ObservationCount: reader.GetInt32(3));

        return Task.FromResult<IReadOnlyDictionary<string, ModelTokenAverage>>(averages);
    }

    /// <summary>
    /// Ensures <see cref="TranscriptDatabase.EnsureCreated"/> has run at least once for this instance.
    /// Startup only creates the schema when capture is enabled at process start
    /// (<c>StartupHealthCheckHostedService</c>); an operator who enables capture later, live, through the
    /// System Settings toggle needs the table to spring into existence on that request rather than on the
    /// next restart. Idempotent and cheap to call repeatedly - <see cref="_schemaEnsured"/> short-circuits
    /// every call after the first real one, so the DDL only ever runs once per process even though every
    /// write path calls this.
    /// </summary>
    private void EnsureSchema()
    {
        if (_schemaEnsured) return;

        lock (_schemaLock)
        {
            if (_schemaEnsured) return;

            _database.EnsureCreated();
            _schemaEnsured = true;
        }
    }

    private static TranscriptRecord ReadTranscriptRecord(SqliteDataReader reader)
    {
        return new TranscriptRecord(
            Id: reader.GetInt64(0),
            CorrelationId: reader.GetString(1),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(2)),
            RequestedModel: reader.GetString(3),
            RoutedModel: reader.GetString(4),
            Dimension: reader.IsDBNull(5) ? null : reader.GetString(5),
            Difficulty: reader.IsDBNull(6) ? null : reader.GetString(6),
            Language: reader.IsDBNull(7) ? null : reader.GetString(7),
            IsUtility: reader.GetInt32(8) != 0,
            PromptText: reader.IsDBNull(9) ? null : reader.GetString(9),
            ResponseText: reader.IsDBNull(10) ? null : reader.GetString(10),
            Score: reader.IsDBNull(11) ? null : reader.GetDouble(11),
            Cost: reader.IsDBNull(12) ? null : (decimal)reader.GetDouble(12),
            IsExploratory: reader.GetInt32(13) != 0,
            Propensity: reader.GetDouble(14),
            InputTokens: reader.IsDBNull(15) ? null : reader.GetInt32(15),
            OutputTokens: reader.IsDBNull(16) ? null : reader.GetInt32(16),
            MemoryEntryId: reader.IsDBNull(17) ? null : reader.GetInt64(17),
            DimBestModel: reader.IsDBNull(18) ? null : reader.GetString(18),
            UntrainedBaselineModel: reader.IsDBNull(19) ? null : reader.GetString(19),
            UntrainedBaselinePredictedScore: reader.IsDBNull(20) ? null : reader.GetDouble(20),
            IsJudgeScored: !reader.IsDBNull(21) && reader.GetInt32(21) != 0,
            ArchiveSessionId: reader.FieldCount > 22 && !reader.IsDBNull(22) && Guid.TryParse(reader.GetString(22), out var sessionId)
                ? sessionId
                : null,
            ArchiveTurnId: reader.FieldCount > 23 && !reader.IsDBNull(23) && Guid.TryParse(reader.GetString(23), out var turnId)
                ? turnId
                : null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<long> DeleteByArchiveSessions(IReadOnlyCollection<Guid> archiveSessionIds)
    {
        ArgumentNullException.ThrowIfNull(archiveSessionIds);
        if (archiveSessionIds.Count == 0) return [];

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var memoryIds = new List<long>();
        foreach (var archiveSessionId in archiveSessionIds.Distinct())
        {
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                                 SELECT memory_entry_id FROM request_transcripts
                                 WHERE archive_session_id = $id AND memory_entry_id IS NOT NULL;
                                 """;
            select.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read()) memoryIds.Add(reader.GetInt64(0));
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM request_transcripts WHERE archive_session_id = $id;";
            delete.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        TruncateWalAfterDelete(connection);
        return memoryIds;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredTurnText>> LoadTurnTextsAsync(
        IReadOnlyList<long> transcriptIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcriptIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.CurrentValue.Enabled || transcriptIds.Count == 0)
            return Task.FromResult<IReadOnlyList<StoredTurnText>>([]);

        EnsureSchema();
        using var connection = _database.OpenConnection();
        var rows = new List<StoredTurnText>(transcriptIds.Count);
        foreach (var id in transcriptIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT archive_session_id, archive_turn_id, prompt_text_length, response_text_length
                                  FROM request_transcripts
                                  WHERE id = $id;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                rows.Add(new StoredTurnText(id, false, null, null, null, null));
                continue;
            }

            SessionExtracts? extracts = null;
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1) &&
                Guid.TryParse(reader.GetString(0), out var sessionId) &&
                Guid.TryParse(reader.GetString(1), out var turnId))
            {
                extracts = ReadExtracts(sessionId, turnId);
            }

            rows.Add(new StoredTurnText(
                id,
                true,
                extracts?.NewestUserMessage,
                extracts?.ResponseText,
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return Task.FromResult<IReadOnlyList<StoredTurnText>>(rows);
    }

    /// <summary>Fills prompt and response text from the session file when this row points at one.</summary>
    private TranscriptRecord WithExtracts(TranscriptRecord record)
    {
        if (record.ArchiveSessionId is not { } sessionId || record.ArchiveTurnId is not { } turnId)
            return record with { PromptText = null, ResponseText = null };

        var extracts = ReadExtracts(sessionId, turnId);
        return record with
        {
            PromptText = extracts?.NewestUserMessage,
            ResponseText = extracts?.ResponseText
        };
    }

    /// <summary>Reads one turn's extracts, or <see langword="null"/> when no reader is wired or the frame is missing.</summary>
    private SessionExtracts? ReadExtracts(Guid archiveSessionId, Guid archiveTurnId)
    {
        if (_extracts is null) return null;

        try
        {
            return _extracts.Value.TryReadExtracts(archiveSessionId, archiveTurnId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read extracts for session {ArchiveSessionId}.", archiveSessionId);
            return null;
        }
    }

    /// <summary>
    /// Counts Unicode scalar values, matching SQLite's <c>length()</c> on text, so an emoji is one character.
    /// </summary>
    private static object CharacterLength(string? text) =>
        text is null ? DBNull.Value : text.EnumerateRunes().Count();
}