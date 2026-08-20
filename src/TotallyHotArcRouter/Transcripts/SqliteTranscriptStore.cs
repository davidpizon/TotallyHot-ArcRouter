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
}
