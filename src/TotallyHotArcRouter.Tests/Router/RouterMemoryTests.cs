using Microsoft.Extensions.Options;
using Moq;
using System.Collections.Concurrent;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Tests for the <see cref="RouterMemory"/> class.
/// </summary>
public class RouterMemoryTests
{
    [Fact]
    public async Task AddScore_And_GetAverageScore_WorkCorrectly()
    {
        var memory = new RouterMemory();
        var dimension = "test_dimension";
        var model = "test_model";

        await memory.AddScoreAsync(dimension, model, 0.8);
        await memory.AddScoreAsync(dimension, model, 0.9);
        var averageScore = memory.GetAverageScore(dimension, model);

        Assert.NotNull(averageScore);
        Assert.Equal(0.85, averageScore.Value, 2);
    }

    [Fact]
    public void GetAverageScore_ReturnsNull_ForUnknownModel()
    {
        var memory = new RouterMemory();

        var averageScore = memory.GetAverageScore("unknown_dimension", "unknown_model");

        Assert.Null(averageScore);
    }

    [Fact]
    public async Task GetModelsForDimension_ReturnsCorrectModels()
    {
        var memory = new RouterMemory();
        var dimension = "test_dimension";
        await memory.AddScoreAsync(dimension, "model1", 0.8);
        await memory.AddScoreAsync(dimension, "model2", 0.9);

        var models = memory.GetModelsForDimension(dimension);

        Assert.Collection(models.OrderBy(m => m),
            m => Assert.Equal("model1", m),
            m => Assert.Equal("model2", m));
    }

    [Fact]
    public async Task InitializeAsync_LoadsScoresFromStore()
    {
        var storeMock = new Mock<IRouterMemoryStore>();
        storeMock.Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>
            {
                ["code_gen"] = new ConcurrentDictionary<string, ScoreAggregate>
                {
                    ["model-a"] = new ScoreAggregate(Sum: 1.6, Count: 2)
                }
            });

        var memory = new RouterMemory(storeMock.Object);

        await memory.InitializeAsync();

        var average = memory.GetAverageScore("code_gen", "model-a");
        Assert.NotNull(average);
        Assert.Equal(0.8, average.Value, 3);
        Assert.Equal(2, memory.GetObservationCount("code_gen", "model-a"));
        storeMock.Verify(s => s.LoadAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddScoreAsync_WithStore_ForwardsTheSingleObservation()
    {
        var storeMock = new Mock<IRouterMemoryStore>();
        var memory = new RouterMemory(storeMock.Object);

        await memory.AddScoreAsync("bug_fix", "model-b", 0.95);

        // The store is handed one observation, not a whole-memory snapshot: that is what lets it fold the
        // score in with a single upsert instead of rewriting the accumulated history.
        storeMock.Verify(
            s => s.RecordScoreAsync("bug_fix", "model-b", 0.95, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetObservationCount_ReportsHowManyScoresBackTheAverage()
    {
        var memory = new RouterMemory();

        await memory.AddScoreAsync("test_dimension", "model-a", 0.2);
        await memory.AddScoreAsync("test_dimension", "model-a", 0.4);
        await memory.AddScoreAsync("test_dimension", "model-a", 0.6);

        Assert.Equal(3, memory.GetObservationCount("test_dimension", "model-a"));
        Assert.Equal(0.4, memory.GetAverageScore("test_dimension", "model-a")!.Value, 3);
    }

    [Fact]
    public void GetObservationCount_ReturnsZero_ForUnknownPair()
    {
        var memory = new RouterMemory();

        Assert.Equal(0, memory.GetObservationCount("unknown_dimension", "unknown_model"));
        Assert.Null(memory.GetAverageScore("unknown_dimension", "unknown_model"));
    }

    [Fact]
    public async Task Persistence_WithSharedStore_SurvivesMemoryRecreation()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(tempDirectory, "router_embedding_memory.db")
            }));
            database.EnsureCreated();

            var store = new SqliteRouterMemoryStore(database);
            var firstMemory = new RouterMemory(store);
            await firstMemory.AddScoreAsync("refactor", "model-c", 0.8);
            await firstMemory.AddScoreAsync("refactor", "model-c", 1.0);

            var secondMemory = new RouterMemory(store);
            await secondMemory.InitializeAsync();

            var average = secondMemory.GetAverageScore("refactor", "model-c");
            Assert.NotNull(average);
            Assert.Equal(0.9, average.Value, 3);
            Assert.Equal(2, secondMemory.GetObservationCount("refactor", "model-c"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // SQLite's connection pool can still hold the file handle; a leftover temp directory is
                // not a test failure. Matches SqliteMemoryEntryStoreTests' best-effort teardown.
            }
        }
    }

    [Fact]
    public async Task AddScoreAsync_WithConcurrentCalls_StoresAllScores()
    {
        var memory = new RouterMemory();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => memory.AddScoreAsync("concurrency", "model-d", i / 100.0));

        await Task.WhenAll(tasks);

        // Asserting the exact count and mean, not merely non-null: the aggregate is updated by an atomic
        // AddOrUpdate, so a lost update under contention is a real failure this test must be able to see.
        // Scores 0.00..0.99 sum to 49.5, so the mean is exactly 0.495.
        Assert.Equal(100, memory.GetObservationCount("concurrency", "model-d"));
        Assert.Equal(0.495, memory.GetAverageScore("concurrency", "model-d")!.Value, 6);
    }

    [Fact]
    public async Task AddScoreAsync_ConcurrentCalls_ForwardEveryScoreToTheStoreExactlyOnce()
    {
        // The in-memory aggregate and the store accumulate the same observations independently, so they
        // agree only if every AddScoreAsync forwards exactly one score. A dropped or duplicated forward
        // would silently desync what routing reads from what a restart would reload.
        var store = new RecordingStoreSpy();
        var memory = new RouterMemory(store);

        var tasks = Enumerable.Range(0, 200)
            .Select(i => memory.AddScoreAsync("concurrency", $"model-{i % 4}", i / 200.0));

        await Task.WhenAll(tasks);

        Assert.Equal(200, store.Recorded.Count);
        Assert.Equal(200, Enumerable.Range(0, 4).Sum(i => memory.GetObservationCount("concurrency", $"model-{i}")));

        foreach (var modelIndex in Enumerable.Range(0, 4))
        {
            var model = $"model-{modelIndex}";
            var recordedSum = store.Recorded.Where(r => r.Model == model).Sum(r => r.Score);
            Assert.Equal(recordedSum, memory.GetAverageScore("concurrency", model)!.Value * memory.GetObservationCount("concurrency", model), 6);
        }
    }

    /// <summary>
    /// An <see cref="IRouterMemoryStore"/> capturing every observation forwarded to it, so a test can
    /// compare what the store was told against what the in-memory aggregate holds.
    /// </summary>
    private sealed class RecordingStoreSpy : IRouterMemoryStore
    {
        private readonly ConcurrentQueue<(string Dimension, string Model, double Score)> _recorded = new();

        /// <summary>Gets every observation forwarded to this store.</summary>
        public IReadOnlyCollection<(string Dimension, string Model, double Score)> Recorded => _recorded;

        /// <inheritdoc />
        public Task<ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>> LoadAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>());

        /// <inheritdoc />
        public Task RecordScoreAsync(
            string dimension,
            string model,
            double score,
            CancellationToken cancellationToken = default)
        {
            _recorded.Enqueue((dimension, model, score));
            return Task.CompletedTask;
        }
    }
}

