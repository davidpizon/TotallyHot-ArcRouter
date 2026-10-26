using System.Globalization;
using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Storage;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// SQLite store for sealed response vectors in <see cref="RouterMemoryDatabase"/>. Plaintext floats are
/// sealed on the way in and opened on the way out. A failed authentication deletes the row.
/// </summary>
public sealed class SqliteResponseVectorStore : IResponseVectorStore, IScoreRetentionStore
{
    private const int DeleteChunk = 500;

    private readonly RouterMemoryDatabase _database;
    private readonly ResponseVectorKeyHolder _keys;
    private readonly ILogger<SqliteResponseVectorStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="SqliteResponseVectorStore"/> class.</summary>
    /// <param name="database">The memory database. Schema is created on first use.</param>
    /// <param name="keys">The vector key. A missing key deletes existing rows and starts a new one.</param>
    /// <param name="logger">The logger.</param>
    public SqliteResponseVectorStore(
        RouterMemoryDatabase database,
        ResponseVectorKeyHolder keys,
        ILogger<SqliteResponseVectorStore> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _keys = keys;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task UpsertAsync(string correlationId, string embeddingModel, float[] vector, DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        ArgumentNullException.ThrowIfNull(vector);
        cancellationToken.ThrowIfCancellationRequested();

        var sealedBlob = ResponseVectorSeal.Seal(key: _keys.GetOrCreate(), correlationId: correlationId,
            modelId: embeddingModel, vector: vector);
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO response_vectors (correlation_id, created_at_utc, embedding_model, vector)
                              VALUES ($correlationId, $createdAtUtc, $embeddingModel, $vector)
                              ON CONFLICT(correlation_id) DO UPDATE SET
                                  created_at_utc = excluded.created_at_utc,
                                  embedding_model = excluded.embedding_model,
                                  vector = excluded.vector;
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        command.Parameters.AddWithValue(parameterName: "$createdAtUtc", value: createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue(parameterName: "$embeddingModel", value: embeddingModel);
        command.Parameters.AddWithValue(parameterName: "$vector", value: sealedBlob);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the row for <paramref name="correlationId"/>. A blob that fails authentication is logged and
    /// deleted, and this returns false.
    /// </summary>
    /// <param name="correlationId">The request correlation id.</param>
    /// <param name="vector">The opened vector when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a row opened.</returns>
    public bool TryOpen(string correlationId, out float[] vector)
    {
        vector = [];
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var row = ReadRow(correlationId);
        if (row is null) return false;

        if (ResponseVectorSeal.TryOpen(key: _keys.GetOrCreate(), correlationId: correlationId,
                modelId: row.Value.ModelId, blob: row.Value.Blob, vector: out vector))
            return true;

        DeleteByCorrelationIds([correlationId]);
        _logger.LogWarning(
            "Response vector for correlation {CorrelationId} failed authentication and was deleted.",
            correlationId);
        vector = [];
        return false;
    }

    /// <summary>Returns the sealed blob, for tests that must show it is not plaintext.</summary>
    /// <param name="correlationId">The request correlation id.</param>
    /// <returns>The sealed blob, or null when no row exists.</returns>
    public byte[]? ReadSealed(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return ReadRow(correlationId)?.Blob;
    }

    /// <inheritdoc/>
    public void DeleteByCorrelationIds(IReadOnlyList<string> correlationIds)
    {
        ArgumentNullException.ThrowIfNull(correlationIds);
        if (correlationIds.Count == 0) return;

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        for (var offset = 0; offset < correlationIds.Count; offset += DeleteChunk)
        {
            var count = Math.Min(val1: DeleteChunk, val2: correlationIds.Count - offset);
            using var command = connection.CreateCommand();
            var names = new string[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameterName: names[i], value: correlationIds[offset + i]);
            }

            command.CommandText = "DELETE FROM response_vectors WHERE correlation_id IN (" +
                                  string.Join(separator: ", ", values: names) + ");";
            command.ExecuteNonQuery();
        }

        Truncate(connection);
    }

    /// <inheritdoc/>
    public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM response_vectors;";
        return Task.FromResult(Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture));
    }

    /// <inheritdoc/>
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= 0) return Task.FromResult(0);

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM response_vectors
                              WHERE correlation_id IN (
                                  SELECT correlation_id FROM response_vectors
                                  ORDER BY created_at_utc ASC LIMIT $count);
                              """;
        command.Parameters.AddWithValue(parameterName: "$count", value: count);
        var deleted = command.ExecuteNonQuery();
        if (deleted > 0) Truncate(connection);
        return Task.FromResult(deleted);
    }

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM response_vectors WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff.ToString("O"));
        var deleted = command.ExecuteNonQuery();
        if (deleted > 0) Truncate(connection);
        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Reads the stored embedding model id without opening the blob. A different model id is not an
    /// authentication failure, so this does not delete the row the way <see cref="TryOpen"/> does.
    /// </summary>
    /// <param name="correlationId">The response vector's correlation id.</param>
    /// <param name="modelId">The stored model id when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a row exists.</returns>
    internal bool TryReadEmbeddingModel(string correlationId, out string modelId)
    {
        modelId = string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var row = ReadRow(correlationId);
        if (row is null) return false;

        modelId = row.Value.ModelId;
        return true;
    }

    /// <summary>Reads one sealed row, or null when it is absent.</summary>
    private (string ModelId, byte[] Blob)? ReadRow(string correlationId)
    {
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT embedding_model, vector FROM response_vectors
                              WHERE correlation_id = $correlationId;
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), (byte[])reader["vector"]);
    }

    /// <summary>Truncates the write-ahead log after a delete so the removal is final.</summary>
    private void Truncate(SqliteConnection connection)
    {
        if (!SqliteHardening.TruncateWal(connection))
            _logger.LogWarning(
                "Response-vector deletion is not yet final: the write-ahead log was busy and will be truncated at the next start.");
    }
}
