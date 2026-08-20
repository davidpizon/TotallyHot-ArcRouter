using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// A SQLite-backed <see cref="ITranscriptStore"/> over <see cref="TranscriptDatabase"/>'s
/// <c>request_transcripts</c> table. Every method checks <see cref="TranscriptOptions.Enabled"/> first and
/// no-ops when capture is disabled, so a caller never needs its own enabled check and no query ever runs
/// against a table that startup deliberately never created.
/// </summary>
public sealed class SqliteTranscriptStore : ITranscriptStore
{
    private readonly TranscriptDatabase _database;
    private readonly TranscriptOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteTranscriptStore"/> class.
    /// </summary>
    /// <param name="database">The database to persist rows in. Its schema must already be created when <see cref="TranscriptOptions.Enabled"/> is <see langword="true"/>.</param>
    /// <param name="options">Supplies <see cref="TranscriptOptions.Enabled"/>.</param>
    public SqliteTranscriptStore(TranscriptDatabase database, IOptions<TranscriptOptions> options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
    }

    /// <inheritdoc />
    public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.FromResult<long?>(null);
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO request_transcripts (
                correlation_id, created_at_utc, requested_model, routed_model, dimension, difficulty,
                language, is_utility, prompt_text, response_text, score, cost, is_exploratory, propensity,
                input_tokens, output_tokens, memory_entry_id)
            VALUES (
                $correlationId, $createdAtUtc, $requestedModel, $routedModel, $dimension, $difficulty,
                $language, $isUtility, $promptText, $responseText, $score, $cost, $isExploratory, $propensity,
                $inputTokens, $outputTokens, $memoryEntryId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$correlationId", record.CorrelationId);
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

        var id = (long)command.ExecuteScalar()!;
        return Task.FromResult<long?>(id);
    }

    /// <inheritdoc />
    public Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

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

        if (!_options.Enabled)
        {
            return Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        }

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
    public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.FromResult<TranscriptRecord?>(null);
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id, correlation_id, created_at_utc, requested_model, routed_model, dimension, difficulty,
                language, is_utility, prompt_text, response_text, score, cost, is_exploratory, propensity,
                input_tokens, output_tokens, memory_entry_id
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

        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

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

        if (!_options.Enabled)
        {
            return Task.FromResult(0);
        }

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

        if (!_options.Enabled)
        {
            return Task.FromResult(0);
        }

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

        if (!_options.Enabled)
        {
            return Task.FromResult(0);
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM request_transcripts WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

        var affectedRows = command.ExecuteNonQuery();
        return Task.FromResult(affectedRows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>());
        }

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

    /// <summary>
    /// Reads a complete <see cref="TranscriptRecord"/> from a reader positioned at a row with all
    /// columns in their standard order.
    /// </summary>
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
            MemoryEntryId: reader.IsDBNull(17) ? null : reader.GetInt64(17));
    }
}
