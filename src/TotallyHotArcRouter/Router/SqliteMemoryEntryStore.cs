using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// A SQLite-backed <see cref="IMemoryEntryStore"/> over <see cref="RouterMemoryDatabase"/>'s
/// <c>memory_entries</c> table. Embedding vectors are stored as a raw little-endian <c>float32</c> BLOB.
/// </summary>
public sealed class SqliteMemoryEntryStore : IMemoryEntryStore
{
    private readonly RouterMemoryDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteMemoryEntryStore"/> class.
    /// </summary>
    /// <param name="database">The database to persist entries in. Its schema must already be created.</param>
    public SqliteMemoryEntryStore(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, embedding, chosen_model, score, cost, verifier_trace, created_at_utc, is_exploratory, propensity, dimension
            FROM memory_entries
            ORDER BY id ASC;
            """;

        var entries = new List<MemoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_entries (embedding, chosen_model, score, cost, verifier_trace, created_at_utc, is_exploratory, propensity, dimension)
            VALUES ($embedding, $chosenModel, $score, $cost, $verifierTrace, $createdAtUtc, $isExploratory, $propensity, $dimension);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$embedding", ToBytes(entry.TaskEmbedding));
        command.Parameters.AddWithValue("$chosenModel", entry.ChosenModel);
        command.Parameters.AddWithValue("$score", entry.Score);
        command.Parameters.AddWithValue("$cost", entry.Cost);
        command.Parameters.AddWithValue("$verifierTrace", (object?)entry.VerifierTrace ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", entry.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$isExploratory", entry.IsExploratory ? 1 : 0);
        command.Parameters.AddWithValue("$propensity", entry.Propensity);
        command.Parameters.AddWithValue("$dimension", (object?)entry.Dimension ?? DBNull.Value);

        var id = (long)command.ExecuteScalar()!;
        return Task.FromResult(entry with { Id = id });
    }

    /// <inheritdoc />
    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <summary>Materializes a <see cref="MemoryEntry"/> from the current row of a <c>memory_entries</c> reader.</summary>
    /// <param name="reader">A reader positioned on a row selected with the columns in <see cref="LoadAllAsync"/>'s query order.</param>
    /// <returns>The row's data as a <see cref="MemoryEntry"/>.</returns>
    private static MemoryEntry ReadEntry(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var embedding = FromBytes((byte[])reader["embedding"]);
        var chosenModel = reader.GetString(2);
        var score = reader.GetDouble(3);
        var cost = reader.GetDouble(4);
        var verifierTrace = reader.IsDBNull(5) ? null : reader.GetString(5);
        var createdAtUtc = DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind);
        var isExploratory = reader.GetInt64(7) != 0;
        var propensity = reader.GetDouble(8);
        var dimension = reader.IsDBNull(9) ? null : reader.GetString(9);

        return new MemoryEntry(id, embedding, chosenModel, score, cost, verifierTrace, createdAtUtc, isExploratory, propensity, dimension);
    }

    /// <summary>Encodes an embedding vector as a raw little-endian <c>float32</c> byte array for storage in the <c>embedding</c> BLOB column.</summary>
    /// <param name="embedding">The vector to encode.</param>
    /// <returns>The encoded bytes.</returns>
    private static byte[] ToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Decodes a raw little-endian <c>float32</c> byte array, the inverse of <see cref="ToBytes"/>.</summary>
    /// <param name="bytes">The bytes read from the <c>embedding</c> BLOB column.</param>
    /// <returns>The decoded embedding vector.</returns>
    private static float[] FromBytes(byte[] bytes)
    {
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
        return embedding;
    }
}
