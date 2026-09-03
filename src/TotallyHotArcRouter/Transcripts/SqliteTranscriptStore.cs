using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// A SQLite-backed <see cref="ITranscriptStore"/> over <see cref="TranscriptDatabase"/>'s
/// <c>request_transcripts</c> table. Every method reads <see cref="TranscriptOptions.Enabled"/> live off
/// <see cref="IOptionsMonitor{TOptions}"/> and no-ops when capture is currently disabled, so a caller never
/// needs its own enabled check and the System Settings window's Transcription Capture toggle takes effect
/// immediately - the same live-gate posture <see cref="Judge.JudgeShadowScoreObserver"/> takes for
/// <see cref="Judge.JudgeOptions.Enabled"/>.
/// </summary>
public sealed class SqliteTranscriptStore : ITranscriptStore
{
    private readonly TranscriptDatabase _database;
    private readonly IOptionsMonitor<TranscriptOptions> _options;
    private readonly object _schemaLock = new();
    private volatile bool _schemaEnsured;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteTranscriptStore"/> class.
    /// </summary>
    /// <param name="database">The database to persist rows in. Its schema is created lazily, on first use, rather than only at startup - see <see cref="EnsureSchema"/>.</param>
    /// <param name="options">Supplies the live <see cref="TranscriptOptions.Enabled"/> gate, read per call rather than captured.</param>
    public SqliteTranscriptStore(TranscriptDatabase database, IOptionsMonitor<TranscriptOptions> options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options;
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
        if (_schemaEnsured)
        {
            return;
        }

        lock (_schemaLock)
        {
            if (_schemaEnsured)
            {
                return;
            }

            _database.EnsureCreated();
            _schemaEnsured = true;
        }
    }

    /// <inheritdoc />
    public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<long?>(null);
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO request_transcripts (
                correlation_id, session_id, created_at_utc, requested_model, routed_model, dimension,
                difficulty, language, is_utility, prompt_text, response_text, score, cost, is_exploratory,
                propensity, input_tokens, output_tokens, memory_entry_id, dim_best_model)
            VALUES (
                $correlationId, $sessionId, $createdAtUtc, $requestedModel, $routedModel, $dimension,
                $difficulty, $language, $isUtility, $promptText, $responseText, $score, $cost,
                $isExploratory, $propensity, $inputTokens, $outputTokens, $memoryEntryId, $dimBestModel);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$correlationId", record.CorrelationId);
        command.Parameters.AddWithValue("$sessionId", CorrelationIdParser.SessionIdOf(record.CorrelationId));
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$requestedModel", record.RequestedModel);
        command.Parameters.AddWithValue("$routedModel", record.RoutedModel);
        command.Parameters.AddWithValue("$dimension", (object?)record.Dimension ?? DBNull.Value);
        command.Parameters.AddWithValue("$difficulty", (object?)record.Difficulty ?? DBNull.Value);
        command.Parameters.AddWithValue("$language", (object?)record.Language ?? DBNull.Value);
        command.Parameters.AddWithValue("$isUtility", record.IsUtility ? 1 : 0);
        command.Parameters.AddWithValue("$promptText", (object?)record.PromptText ?? DBNull.Value);
        command.Parameters.AddWithValue("$responseText", (object?)record.ResponseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$score", (object?)record.Score ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost", (object?)record.Cost ?? DBNull.Value);
        command.Parameters.AddWithValue("$isExploratory", record.IsExploratory ? 1 : 0);
        command.Parameters.AddWithValue("$propensity", record.Propensity);
        command.Parameters.AddWithValue("$inputTokens", (object?)record.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", (object?)record.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$memoryEntryId", (object?)record.MemoryEntryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dimBestModel", (object?)record.DimBestModel ?? DBNull.Value);

        var id = (long)command.ExecuteScalar()!;
        return Task.FromResult<long?>(id);
    }

    /// <inheritdoc />
    public Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.CompletedTask;
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        // Updates every matching row, not just the newest - in the ordinary case there is exactly one
        // (correlation ids are per-request), and this avoids a second query to find "the" row when more
        // than one somehow shares an id.
        command.CommandText = "UPDATE request_transcripts SET score = $score WHERE correlation_id = $correlationId;";
        command.Parameters.AddWithValue("$score", (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", correlationId);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM request_transcripts
            WHERE memory_entry_id IS NULL AND score IS NOT NULL
            ORDER BY id ASC
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
    public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(
        string scorerVersion,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scorerVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        // "IS NOT $scorerVersion" rather than "<> $scorerVersion": SQL's three-valued logic makes
        // `NULL <> 'x'` evaluate to NULL, not true, so a plain inequality would silently exclude every
        // never-scanned row - exactly the rows this sweep exists to find.
        command.CommandText = """
            SELECT id FROM request_transcripts
            WHERE response_text IS NOT NULL
              AND scorer_version IS NOT $scorerVersion
            ORDER BY id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$scorerVersion", scorerVersion);
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
    public Task MarkQualityRescannedAsync(
        long transcriptId,
        string scorerVersion,
        double? score,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transcriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scorerVersion);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.CompletedTask;
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE request_transcripts
            SET score = $score, scorer_version = $scorerVersion
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$score", (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue("$scorerVersion", scorerVersion);
        command.Parameters.AddWithValue("$id", transcriptId);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<TranscriptRecord?>(null);
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id, correlation_id, created_at_utc, requested_model, routed_model, dimension, difficulty,
                language, is_utility, prompt_text, response_text, score, cost, is_exploratory, propensity,
                input_tokens, output_tokens, memory_entry_id, dim_best_model
            FROM request_transcripts
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<TranscriptRecord?>(null);
        }

        var record = ReadTranscriptRecord(reader);
        return Task.FromResult<TranscriptRecord?>(record);
    }

    /// <inheritdoc />
    public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transcriptId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryEntryId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.CompletedTask;
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE request_transcripts SET memory_entry_id = $memoryEntryId WHERE id = $transcriptId;";
        command.Parameters.AddWithValue("$memoryEntryId", memoryEntryId);
        command.Parameters.AddWithValue("$transcriptId", transcriptId);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult(0);
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM request_transcripts;";

        var count = (long)command.ExecuteScalar()!;
        return Task.FromResult((int)count);
    }

    /// <inheritdoc />
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult(0);
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM request_transcripts
            WHERE id IN (SELECT id FROM request_transcripts ORDER BY id ASC LIMIT $count);
            """;
        command.Parameters.AddWithValue("$count", count);

        var affectedRows = command.ExecuteNonQuery();
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc />
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult(0);
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM request_transcripts WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

        var affectedRows = command.ExecuteNonQuery();
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc />
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
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>());
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_entry_id, prompt_text
            FROM request_transcripts
            WHERE memory_entry_id IS NOT NULL AND prompt_text IS NOT NULL;
            """;

        var promptTextByMemoryEntryId = new Dictionary<long, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            promptTextByMemoryEntryId[reader.GetInt64(0)] = reader.GetString(1);
        }

        return Task.FromResult<IReadOnlyDictionary<long, string>>(promptTextByMemoryEntryId);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionTranscript>> ListSessionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<IReadOnlyList<SessionTranscript>>(Array.Empty<SessionTranscript>());
        }

        EnsureSchema();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id, session_id, correlation_id, created_at_utc, requested_model, routed_model,
                prompt_text, response_text, cost, input_tokens, output_tokens, memory_entry_id
            FROM request_transcripts
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<SessionTranscript>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SessionTranscript(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                CorrelationId: reader.GetString(2),
                CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(3)),
                RequestedModel: reader.GetString(4),
                RoutedModel: reader.GetString(5),
                PromptText: reader.IsDBNull(6) ? null : reader.GetString(6),
                ResponseText: reader.IsDBNull(7) ? null : reader.GetString(7),
                Cost: reader.IsDBNull(8) ? null : (decimal)reader.GetDouble(8),
                InputTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                OutputTokens: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                MemoryEntryId: reader.IsDBNull(11) ? null : reader.GetInt64(11)));
        }

        return Task.FromResult<IReadOnlyList<SessionTranscript>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ModelTokenAverage>>(
                new Dictionary<string, ModelTokenAverage>(StringComparer.Ordinal));
        }

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
        {
            averages[reader.GetString(0)] = new ModelTokenAverage(
                reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3));
        }

        return Task.FromResult<IReadOnlyDictionary<string, ModelTokenAverage>>(averages);
    }

    private static TranscriptRecord ReadTranscriptRecord(Microsoft.Data.Sqlite.SqliteDataReader reader)
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
            DimBestModel: reader.IsDBNull(18) ? null : reader.GetString(18));
    }
}
