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
    private readonly string _tempDirectory;
    private readonly string _memoryDatabasePath;

    public PersistenceRegressionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"router_persistence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _memoryDatabasePath = Path.Combine(_tempDirectory, "router_embedding_memory.db");
    }

    [Fact]
    public async Task SqliteMemoryStore_PersistsScores_AcrossMemoryRecreation()
    {
        var database = CreateDatabase();
        var store = new SqliteRouterMemoryStore(database);

        var firstMemory = new RouterMemory(store, new NullLogger<RouterMemory>());
        await firstMemory.AddScoreAsync("bug_fix", "gpt-5.4", 0.8);
        await firstMemory.AddScoreAsync("bug_fix", "gpt-5.4", 1.0);

        var secondMemory = new RouterMemory(store, new NullLogger<RouterMemory>());
        await secondMemory.InitializeAsync();

        var average = secondMemory.GetAverageScore("bug_fix", "gpt-5.4");
        Assert.NotNull(average);
        Assert.Equal(0.9, average.Value, 3);
    }

    [Fact]
    public async Task SqliteMemoryStore_SurvivesAFreshDatabaseHandle_AsAProcessRestartWould()
    {
        // The scenario the JSON store could not survive: a second process-lifetime opening the same file.
        // Constructing an entirely new RouterMemoryDatabase (not just a new RouterMemory over the same
        // store instance) is what makes this a restart rather than an in-process reload.
        var firstMemory = new RouterMemory(new SqliteRouterMemoryStore(CreateDatabase()), new NullLogger<RouterMemory>());
        await firstMemory.AddScoreAsync("code_generation", "claude-opus-4.6", 0.75);

        var secondMemory = new RouterMemory(new SqliteRouterMemoryStore(CreateDatabase()), new NullLogger<RouterMemory>());
        await secondMemory.InitializeAsync();

        Assert.Equal(0.75, secondMemory.GetAverageScore("code_generation", "claude-opus-4.6")!.Value, 3);
        Assert.Equal(1, secondMemory.GetObservationCount("code_generation", "claude-opus-4.6"));
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

    public void Dispose()
    {
        // Scoped ClearPool, matching SqliteMemoryEntryStoreTests: SQLite's connection pool keeps the file
        // handle alive after the last connection is disposed, so the directory delete below fails without it.
        try
        {
            using var connection = CreateDatabase().OpenConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
        }

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}

