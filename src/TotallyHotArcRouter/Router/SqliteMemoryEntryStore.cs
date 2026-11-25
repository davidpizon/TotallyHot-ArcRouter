using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Storage;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// A SQLite-backed <see cref="IMemoryEntryStore"/> over <see cref="RouterMemoryDatabase"/>'s
/// <c>memory_entries</c> table. A new embedding is sealed with the session master key (ADR-0029).
/// A blob written before that change is still a raw little-endian <c>float32</c> vector and still loads.
/// kNN does not decrypt: <see cref="EmbeddingMemory"/> searches the plaintext copy this store returns.
/// </summary>
public sealed class SqliteMemoryEntryStore : IMemoryEntryStore, IQuestionVectorRewrap
{
    private readonly RouterMemoryDatabase _database;
    private readonly ILogger<SqliteMemoryEntryStore> _logger;
    private readonly ISessionMasterKeyStore? _masterKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteMemoryEntryStore"/> class.
    /// </summary>
    /// <param name="database">The database to persist entries in. Its schema must already be created.</param>
    /// <param name="logger">
    /// Reports a log truncation that could not complete after a delete. Optional so a store built by hand
    /// in a test needs no logging setup.
    /// </param>
    /// <param name="masterKeys">
    /// The session master key that seals new embeddings. Null keeps the raw float32 write, which the
    /// tests that predate ADR-0029 rely on. Production passes the store.
    /// </param>
    public SqliteMemoryEntryStore(
        RouterMemoryDatabase database,
        ILogger<SqliteMemoryEntryStore>? logger = null,
        ISessionMasterKeyStore? masterKeys = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = logger ?? NullLogger<SqliteMemoryEntryStore>.Instance;
        _masterKeys = masterKeys;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, embedding, chosen_model, score, cost, verifier_trace, created_at_utc, is_exploratory, propensity, dimension, is_judge_scored, embedding_model, correlation_id
                              FROM memory_entries
                              ORDER BY id ASC;
                              """;

        var entries = new List<MemoryEntry>();
        var key = _masterKeys?.TryGetCurrent();
        var skipped = 0;
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = ReadEntry(reader: reader, key: key);
                if (entry is null) skipped++;
                else entries.Add(entry);
            }
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "Skipped {SkippedCount} sealed question vectors that did not open under the session master key.",
                skipped);
        }

        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }

    /// <inheritdoc/>
    public Task<MemoryEntry?> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Issue #304 phase 5: re-check disputed inside the same transaction as the insert so a dispute
        // that lands between ObserveAsync and this write cannot slip a neighbor row in.
        if (!string.IsNullOrEmpty(entry.CorrelationId) &&
            IsDisputed(connection: connection, transaction: transaction, correlationId: entry.CorrelationId))
        {
            transaction.Commit();
            _logger.LogDebug(
                message:
                "Skipped embedding-memory append for disputed correlation {CorrelationId}.",
                entry.CorrelationId);
            return Task.FromResult<MemoryEntry?>(null);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO memory_entries (embedding, chosen_model, score, cost, verifier_trace, created_at_utc, is_exploratory, propensity, dimension, is_judge_scored, embedding_model, correlation_id)
                              VALUES ($embedding, $chosenModel, $score, $cost, $verifierTrace, $createdAtUtc, $isExploratory, $propensity, $dimension, $isJudgeScored, $embeddingModel, $correlationId);
                              SELECT last_insert_rowid();
                              """;
        command.Parameters.AddWithValue(parameterName: "$embedding",
            value: _masterKeys is null ? ToBytes(entry.TaskEmbedding) : QuestionVectorSeal.Placeholder);
        command.Parameters.AddWithValue(parameterName: "$chosenModel", value: entry.ChosenModel);
        command.Parameters.AddWithValue(parameterName: "$score", value: entry.Score);
        command.Parameters.AddWithValue(parameterName: "$cost", value: (object?)entry.Cost ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$verifierTrace",
            value: (object?)entry.VerifierTrace ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$createdAtUtc", value: entry.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(parameterName: "$isExploratory", value: entry.IsExploratory ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$propensity", value: entry.Propensity);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: (object?)entry.Dimension ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$isJudgeScored", value: entry.IsJudgeScored ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$embeddingModel",
            value: (object?)entry.EmbeddingModel ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$correlationId",
            value: (object?)entry.CorrelationId ?? DBNull.Value);

        var id = (long)command.ExecuteScalar()!;
        if (_masterKeys is not null)
        {
            var key = _masterKeys.GetOrCreateCurrent();
            try
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE memory_entries SET embedding = $embedding WHERE id = $id;";
                update.Parameters.AddWithValue(parameterName: "$embedding",
                    value: QuestionVectorSeal.Seal(key: key, id: id, embeddingModel: entry.EmbeddingModel,
                        vector: entry.TaskEmbedding));
                update.Parameters.AddWithValue(parameterName: "$id", value: id);
                update.ExecuteNonQuery();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        transaction.Commit();
        return Task.FromResult<MemoryEntry?>(entry with { Id = id });
    }

    /// <inheritdoc/>
    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_entries WHERE id = $id;";
        command.Parameters.AddWithValue(parameterName: "$id", value: id);
        command.ExecuteNonQuery();
        TruncateWalAfterDelete(connection);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One transaction and one <c>wal_checkpoint(TRUNCATE)</c> for the whole batch. A busy log is left for
    /// the startup checkpoint to finish, since the next eviction will not necessarily come soon.
    /// </remarks>
    public Task DeleteManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();

        if (ids.Count == 0) return Task.CompletedTask;

        using var connection = _database.OpenConnection();
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM memory_entries WHERE id = $id;";
            var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids)
            {
                // Rechecked per row, as the per-row DeleteAsync loop this replaces did: cancelling throws, the
                // transaction is disposed without a commit, and the whole batch rolls back promptly.
                cancellationToken.ThrowIfCancellationRequested();
                idParameter.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        TruncateWalAfterDelete(connection);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether <paramref name="correlationId"/> is already disputed on <c>grade_receipts</c>, checked
    /// under <paramref name="transaction"/> so the following insert sees the same snapshot.
    /// </summary>
    private static bool IsDisputed(SqliteConnection connection, SqliteTransaction transaction, string correlationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT 1 FROM grade_receipts
                              WHERE correlation_id = $correlationId
                                AND incomplete_reason = $reason
                              LIMIT 1;
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        command.Parameters.AddWithValue(parameterName: "$reason", value: IncompleteReasons.Disputed);
        return command.ExecuteScalar() is not null;
    }


    /// <summary>
    /// Truncates the write-ahead log right after a delete so evicted vectors cannot survive in the
    /// <c>-wal</c> file. A busy log is not an error: the delete succeeded and the startup checkpoint
    /// finishes the job, so it is logged and not retried here.
    /// </summary>
    /// <param name="connection">The connection that ran the delete.</param>
    private void TruncateWalAfterDelete(SqliteConnection connection)
    {
        if (!SqliteHardening.TruncateWal(connection))
            _logger.LogWarning("Embedding memory deletion is not yet final: the write-ahead log was busy and will be truncated at the next start.");
    }

    /// <inheritdoc/>
    public void Rewrap(byte[] fromKey, byte[] toKey)
    {
        ArgumentNullException.ThrowIfNull(fromKey);
        ArgumentNullException.ThrowIfNull(toKey);
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var updates = new List<(long Id, byte[] Blob)>();
        var deletes = new List<long>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, embedding, embedding_model FROM memory_entries;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var blob = (byte[])reader.GetValue(1);
                var model = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!QuestionVectorSeal.IsSealed(blob)) continue;
                if (QuestionVectorSeal.TryOpen(key: toKey, id: id, embeddingModel: model, blob: blob,
                        vector: out _))
                    continue;
                if (QuestionVectorSeal.TryOpen(key: fromKey, id: id, embeddingModel: model, blob: blob,
                        vector: out var opened))
                    updates.Add((id, QuestionVectorSeal.Seal(key: toKey, id: id, embeddingModel: model,
                        vector: opened)));
                else
                    deletes.Add(id);
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE memory_entries SET embedding = $embedding WHERE id = $id;";
            var blobParameter = update.Parameters.Add("$embedding", SqliteType.Blob);
            var idParameter = update.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, blob) in updates)
            {
                blobParameter.Value = blob;
                idParameter.Value = id;
                update.ExecuteNonQuery();
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM memory_entries WHERE id = $id;";
            var idParameter = delete.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in deletes)
            {
                idParameter.Value = id;
                delete.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        if (deletes.Count > 0)
        {
            _logger.LogInformation(
                "Deleted {DeletedCount} sealed question vectors that opened under neither master key.",
                deletes.Count);
        }
    }

    /// <inheritdoc/>
    public bool HasBlobOnlyUnder(byte[] key, byte[] other)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(other);
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, embedding, embedding_model FROM memory_entries;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(1);
            if (!QuestionVectorSeal.IsSealed(blob)) continue;
            var id = reader.GetInt64(0);
            var model = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (QuestionVectorSeal.TryOpen(key: key, id: id, embeddingModel: model, blob: blob, vector: out _) &&
                !QuestionVectorSeal.TryOpen(key: other, id: id, embeddingModel: model, blob: blob, vector: out _))
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public Task<long> GetMaxIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM memory_entries;";
        return Task.FromResult((long)command.ExecuteScalar()!);
    }

    /// <summary>Materializes a <see cref="MemoryEntry"/> from the current row of a <c>memory_entries</c> reader.</summary>
    /// <param name="reader">
    /// A reader positioned on a row selected with the columns in <see cref="LoadAllAsync"/>'s query
    /// order.
    /// </param>
    /// <param name="key">The session master key, or null when the row is expected to be raw float32.</param>
    /// <returns>The row, or null when a sealed blob does not open. A sealed miss is not returned as floats.</returns>
    private static MemoryEntry? ReadEntry(SqliteDataReader reader, byte[]? key)
    {
        var id = reader.GetInt64(0);
        var embeddingModel = reader.IsDBNull(11) ? null : reader.GetString(11);
        var embedding = QuestionVectorSeal.Read(blob: (byte[])reader["embedding"], key: key, id: id,
            embeddingModel: embeddingModel);
        if (embedding is null) return null;
        var chosenModel = reader.GetString(2);
        var score = reader.GetDouble(3);
        var cost = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4);
        var verifierTrace = reader.IsDBNull(5) ? null : reader.GetString(5);
        var createdAtUtc = DateTimeOffset.Parse(input: reader.GetString(6),
            formatProvider: CultureInfo.InvariantCulture, styles: DateTimeStyles.RoundtripKind);
        var isExploratory = reader.GetInt64(7) != 0;
        var propensity = reader.GetDouble(8);
        var dimension = reader.IsDBNull(9) ? null : reader.GetString(9);
        var isJudgeScored = reader.GetInt64(10) != 0;
        var correlationId = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null;

        return new MemoryEntry(Id: id, TaskEmbedding: embedding, ChosenModel: chosenModel, Score: score, Cost: cost,
            VerifierTrace: verifierTrace, CreatedAtUtc: createdAtUtc, IsExploratory: isExploratory,
            Propensity: propensity, Dimension: dimension, IsJudgeScored: isJudgeScored,
            EmbeddingModel: embeddingModel, CorrelationId: correlationId);
    }

    /// <summary>
    /// Encodes an embedding as raw little-endian <c>float32</c>. Used only when no session master key
    /// is configured, so existing tests keep writing the pre-ADR-0029 blob.
    /// </summary>
    /// <param name="embedding">The vector to encode.</param>
    /// <returns>The encoded bytes.</returns>
    private static byte[] ToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(src: embedding, 0, dst: bytes, 0, count: bytes.Length);
        return bytes;
    }
}