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

        await memory.AddScoreAsync(dimension: dimension, model: model, 0.8);
        await memory.AddScoreAsync(dimension: dimension, model: model, 0.9);
        var averageScore = memory.GetAverageScore(dimension: dimension, model: model);

        Assert.NotNull(averageScore);
        Assert.Equal(0.85, actual: averageScore.Value, 2);
    }

    [Fact]
    public void GetAverageScore_ReturnsNull_ForUnknownModel()
    {
        var memory = new RouterMemory();

        var averageScore = memory.GetAverageScore(dimension: "unknown_dimension", model: "unknown_model");

        Assert.Null(averageScore);
    }

    [Fact]
    public async Task GetModelsForDimension_ReturnsCorrectModels()
    {
        var memory = new RouterMemory();
        var dimension = "test_dimension";
        await memory.AddScoreAsync(dimension: dimension, model: "model1", 0.8);
        await memory.AddScoreAsync(dimension: dimension, model: "model2", 0.9);

        var models = memory.GetModelsForDimension(dimension);

        Assert.Collection(collection: models.OrderBy(m => m),
            m => Assert.Equal(expected: "model1", actual: m),
            m => Assert.Equal(expected: "model2", actual: m));
    }

    [Fact]
    public async Task InitializeAsync_LoadsScoresFromStore()
    {
        var storeMock = new Mock<IRouterMemoryStore>();
        storeMock.Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>
            {
                ["code_gen"] = new()
                {
                    ["model-a"] = new ScoreAggregate(1.6, 2)
                }
            });

        var memory = new RouterMemory(storeMock.Object);

        await memory.InitializeAsync();

        var average = memory.GetAverageScore(dimension: "code_gen", model: "model-a");
        Assert.NotNull(average);
        Assert.Equal(0.8, actual: average.Value, 3);
        Assert.Equal(2, actual: memory.GetObservationCount(dimension: "code_gen", model: "model-a"));
        storeMock.Verify(expression: s => s.LoadAllAsync(It.IsAny<CancellationToken>()), times: Times.Once);
    }

    [Fact]
    public async Task AddScoreAsync_WithStore_ForwardsTheSingleObservation()
    {
        var storeMock = new Mock<IRouterMemoryStore>();
        var memory = new RouterMemory(storeMock.Object);

        await memory.AddScoreAsync(dimension: "bug_fix", model: "model-b", 0.95);

        // The store is handed one observation, not a whole-memory snapshot: that is what lets it fold the
        // score in with a single upsert instead of rewriting the accumulated history.
        storeMock.Verify(
            expression: s => s.RecordScoreAsync("bug_fix", "model-b", 0.95, It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    [Fact]
    public async Task GetObservationCount_ReportsHowManyScoresBackTheAverage()
    {
        var memory = new RouterMemory();

        await memory.AddScoreAsync(dimension: "test_dimension", model: "model-a", 0.2);
        await memory.AddScoreAsync(dimension: "test_dimension", model: "model-a", 0.4);
        await memory.AddScoreAsync(dimension: "test_dimension", model: "model-a", 0.6);

        Assert.Equal(3, actual: memory.GetObservationCount(dimension: "test_dimension", model: "model-a"));
        Assert.Equal(0.4, actual: memory.GetAverageScore(dimension: "test_dimension", model: "model-a")!.Value, 3);
    }

    [Fact]
    public void GetObservationCount_ReturnsZero_ForUnknownPair()
    {
        var memory = new RouterMemory();

        Assert.Equal(0, actual: memory.GetObservationCount(dimension: "unknown_dimension", model: "unknown_model"));
        Assert.Null(memory.GetAverageScore(dimension: "unknown_dimension", model: "unknown_model"));
    }

    [Fact]
    public async Task Persistence_WithSharedStore_SurvivesMemoryRecreation()
    {
        var tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        try
        {
            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(path1: tempDirectory, path2: "router_embedding_memory.db")
            }));
            database.EnsureCreated();

            var store = new SqliteRouterMemoryStore(database);
            var firstMemory = new RouterMemory(store);
            await firstMemory.AddScoreAsync(dimension: "refactor", model: "model-c", 0.8);
            await firstMemory.AddScoreAsync(dimension: "refactor", model: "model-c", 1.0);

            var secondMemory = new RouterMemory(store);
            await secondMemory.InitializeAsync();

            var average = secondMemory.GetAverageScore(dimension: "refactor", model: "model-c");
            Assert.NotNull(average);
            Assert.Equal(0.9, actual: average.Value, 3);
            Assert.Equal(2, actual: secondMemory.GetObservationCount(dimension: "refactor", model: "model-c"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory)) Directory.Delete(path: tempDirectory, true);
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
            .Select(i => memory.AddScoreAsync(dimension: "concurrency", model: "model-d", score: i / 100.0));

        await Task.WhenAll(tasks);

        // Asserting the exact count and mean, not merely non-null: the aggregate is updated by an atomic
        // AddOrUpdate, so a lost update under contention is a real failure this test must be able to see.
        // Scores 0.00..0.99 sum to 49.5, so the mean is exactly 0.495.
        Assert.Equal(100, actual: memory.GetObservationCount(dimension: "concurrency", model: "model-d"));
        Assert.Equal(0.495, actual: memory.GetAverageScore(dimension: "concurrency", model: "model-d")!.Value, 6);
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
            .Select(i => memory.AddScoreAsync(dimension: "concurrency", model: $"model-{i % 4}", score: i / 200.0));

        await Task.WhenAll(tasks);

        Assert.Equal(200, actual: store.Recorded.Count);
        Assert.Equal(200,
            actual: Enumerable.Range(0, 4)
                .Sum(i => memory.GetObservationCount(dimension: "concurrency", model: $"model-{i}")));

        foreach (var modelIndex in Enumerable.Range(0, 4))
        {
            var model = $"model-{modelIndex}";
            var recordedSum = store.Recorded.Where(r => r.Model == model).Sum(r => r.Score);
            Assert.Equal(expected: recordedSum,
                actual: memory.GetAverageScore(dimension: "concurrency", model: model)!.Value *
                        memory.GetObservationCount(dimension: "concurrency", model: model), 6);
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

        /// <inheritdoc/>
        public Task<ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>> LoadAllAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>());
        }

        /// <inheritdoc/>
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