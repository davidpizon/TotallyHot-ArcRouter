using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Covers regression behavior for router memory persistence across process-like restarts.
/// </summary>
[Collection("Integration")]
public sealed class PersistenceRegressionTests : IDisposable
{
    private readonly string _memoryDatabasePath;
    private readonly string _tempDirectory;

    public PersistenceRegressionTests()
    {
        _tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: $"router_persistence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _memoryDatabasePath = Path.Combine(path1: _tempDirectory, path2: "router_embedding_memory.db");
    }

    public void Dispose()
    {
        // Scoped ClearPool, matching SqliteMemoryEntryStoreTests: SQLite's connection pool keeps the file
        // handle alive after the last connection is disposed, so the directory delete below fails without it.
        try
        {
            using var connection = CreateDatabase().OpenConnection();
            SqliteConnection.ClearPool(connection);
        }
        catch (SqliteException)
        {
            // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
        }

        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(path: _tempDirectory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SqliteMemoryStore_PersistsScores_AcrossMemoryRecreation()
    {
        var database = CreateDatabase();
        var store = new SqliteRouterMemoryStore(database);

        var firstMemory = new RouterMemory(memoryStore: store, logger: new NullLogger<RouterMemory>());
        await firstMemory.AddScoreAsync(dimension: "bug_fix", model: "gpt-5.4", 0.8);
        await firstMemory.AddScoreAsync(dimension: "bug_fix", model: "gpt-5.4", 1.0);

        var secondMemory = new RouterMemory(memoryStore: store, logger: new NullLogger<RouterMemory>());
        await secondMemory.InitializeAsync();

        var average = secondMemory.GetAverageScore(dimension: "bug_fix", model: "gpt-5.4");
        Assert.NotNull(average);
        Assert.Equal(0.9, actual: average.Value, 3);
    }

    [Fact]
    public async Task SqliteMemoryStore_SurvivesAFreshDatabaseHandle_AsAProcessRestartWould()
    {
        // The scenario the JSON store could not survive: a second process-lifetime opening the same file.
        // Constructing an entirely new RouterMemoryDatabase (not just a new RouterMemory over the same
        // store instance) is what makes this a restart rather than an in-process reload.
        var firstMemory = new RouterMemory(memoryStore: new SqliteRouterMemoryStore(CreateDatabase()),
            logger: new NullLogger<RouterMemory>());
        await firstMemory.AddScoreAsync(dimension: "code_generation", model: "claude-opus-4.6", 0.75);

        var secondMemory = new RouterMemory(memoryStore: new SqliteRouterMemoryStore(CreateDatabase()),
            logger: new NullLogger<RouterMemory>());
        await secondMemory.InitializeAsync();

        Assert.Equal(0.75,
            actual: secondMemory.GetAverageScore(dimension: "code_generation", model: "claude-opus-4.6")!.Value, 3);
        Assert.Equal(1,
            actual: secondMemory.GetObservationCount(dimension: "code_generation", model: "claude-opus-4.6"));
    }

    /// <summary>Opens the shared temp database, creating its schema. Each call is an independent handle.</summary>
    private RouterMemoryDatabase CreateDatabase()
    {
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = _memoryDatabasePath
        }));
        database.EnsureCreated();
        return database;
    }
}