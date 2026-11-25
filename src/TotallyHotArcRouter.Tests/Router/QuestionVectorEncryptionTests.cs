using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.Sessions;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// ADR-0029: a new <c>memory_entries.embedding</c> blob is not raw float32, kNN still sees the
/// plaintext neighbor after load, and a master-key change that does not rewrap leaves no usable vector.
/// </summary>
public sealed class QuestionVectorEncryptionTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(TestScratchDirectory.RunRoot, "question-vectors-" + Guid.NewGuid().ToString("N"));

    private readonly RouterMemoryDatabase _database;

    public QuestionVectorEncryptionTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = Path.Combine(_tempDirectory, "router_embedding_memory.db")
        }));
        _database.EnsureCreated();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            using var connection = _database.OpenConnection();
            SqliteConnection.ClearPool(connection);
        }
        catch (SqliteException)
        {
            // Best-effort cleanup under parallel test runs.
        }

        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
        }
        catch (IOException)
        {
            // A locked file on a busy box is not a test failure.
        }
    }

    [Fact]
    public async Task FreshRow_IsNotReadableAsFloat32WithoutTheMasterKey()
    {
        var keys = new InMemoryMasterKeyStore();
        var store = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, keys);
        var vector = new[] { 0.25f, -0.5f, 0.75f };
        var saved = await store.AppendAsync(
            entry: new MemoryEntry(0, vector, "neighbor", 0.8, 0.01, null, DateTimeOffset.UtcNow,
                EmbeddingModel: "bge-test"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(saved);
        var blob = ReadBlob(saved.Id);
        Assert.True(QuestionVectorSeal.IsSealed(blob));
        var asFloats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(src: blob, srcOffset: 0, dst: asFloats, dstOffset: 0, count: blob.Length);
        Assert.False(vector.AsSpan().SequenceEqual(asFloats));
        Assert.False(QuestionVectorSeal.TryOpen(key: new InMemoryMasterKeyStore().GetOrCreateCurrent(), id: saved.Id,
            embeddingModel: "bge-test", blob: blob, vector: out _));
    }

    [Fact]
    public async Task InitializeAsync_ReturnsTheSameNeighbor()
    {
        var keys = new InMemoryMasterKeyStore();
        var store = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, keys);
        var vector = new[] { 1f, 0f, 0f };
        await store.AppendAsync(
            entry: new MemoryEntry(0, vector, "neighbor", 0.8, 0.01, null, DateTimeOffset.UtcNow,
                EmbeddingModel: "bge-test"),
            cancellationToken: TestContext.Current.CancellationToken);

        var memory = new EmbeddingMemory(store,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            {
                EmbeddingSimilarityThreshold = -1,
                MaxNeighborCount = 4,
                EmbeddingMemoryCapacity = 100
            }),
            new StubEmbeddingClient("bge-test"),
            NullLogger<EmbeddingMemory>.Instance);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var hit = Assert.Single(memory.FindNearest(vector));
        Assert.Equal("neighbor", hit.Entry.ChosenModel);
        Assert.Equal(vector, hit.Entry.TaskEmbedding);
    }

    [Fact]
    public async Task RotationWithoutRewrap_LeavesNoUsableVector()
    {
        var original = new InMemoryMasterKeyStore();
        var writer = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, original);
        var vector = new[] { 0.2f, 0.3f, 0.4f };
        await writer.AppendAsync(
            entry: new MemoryEntry(0, vector, "neighbor", 0.8, 0.01, null, DateTimeOffset.UtcNow,
                EmbeddingModel: "bge-test"),
            cancellationToken: TestContext.Current.CancellationToken);

        var rotated = new InMemoryMasterKeyStore();
        var reader = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, rotated);
        var loaded = await reader.LoadAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(loaded);

        var memory = new EmbeddingMemory(reader,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            {
                EmbeddingSimilarityThreshold = -1,
                MaxNeighborCount = 4,
                EmbeddingMemoryCapacity = 100
            }),
            new StubEmbeddingClient("bge-test"),
            NullLogger<EmbeddingMemory>.Instance);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(memory.FindNearest(vector));
    }

    [Fact]
    public async Task Rewrap_KeepsTheNeighborUnderTheNewKey()
    {
        var original = new InMemoryMasterKeyStore();
        var rotated = new InMemoryMasterKeyStore();
        var store = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, original);
        var vector = new[] { 0.2f, 0.3f, 0.4f };
        await store.AppendAsync(
            entry: new MemoryEntry(0, vector, "neighbor", 0.8, 0.01, null, DateTimeOffset.UtcNow,
                EmbeddingModel: "bge-test"),
            cancellationToken: TestContext.Current.CancellationToken);

        store.Rewrap(fromKey: original.GetOrCreateCurrent(), toKey: rotated.GetOrCreateCurrent());

        var reader = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, rotated);
        var loaded = Assert.Single(await reader.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(vector, loaded.TaskEmbedding);
    }

    [Fact]
    public async Task LegacyPlaintextRow_StillLoadsWhenAKeyIsConfigured()
    {
        var keys = new InMemoryMasterKeyStore();
        var vector = new[] { 0.1f, 0.2f };
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(src: vector, srcOffset: 0, dst: bytes, dstOffset: 0, count: bytes.Length);
        await using (var connection = _database.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO memory_entries
                                      (embedding, chosen_model, score, cost, created_at_utc)
                                  VALUES ($embedding, 'legacy', 0.5, 0.0, $created);
                                  """;
            command.Parameters.AddWithValue("$embedding", bytes);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var store = new SqliteMemoryEntryStore(_database, NullLogger<SqliteMemoryEntryStore>.Instance, keys);
        var loaded = Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(vector, loaded.TaskEmbedding);
        Assert.Equal("legacy", loaded.ChosenModel);
    }

    private byte[] ReadBlob(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT embedding FROM memory_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])command.ExecuteScalar()!;
    }
}
