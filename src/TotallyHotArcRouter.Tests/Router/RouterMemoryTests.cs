using TotallyHot.ArcRouter.Router;
using Moq;
using System.Collections.Concurrent;

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
        storeMock.Setup(s => s.LoadAsync()).ReturnsAsync(
            new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>
            {
                ["code_gen"] = new ConcurrentDictionary<string, List<double>>
                {
                    ["model-a"] = [0.7, 0.9]
                }
            });

        var memory = new RouterMemory(storeMock.Object);

        await memory.InitializeAsync();

        var average = memory.GetAverageScore("code_gen", "model-a");
        Assert.NotNull(average);
        Assert.Equal(0.8, average.Value, 3);
        storeMock.Verify(s => s.LoadAsync(), Times.Once);
    }

    [Fact]
    public async Task AddScoreAsync_WithStore_SavesUpdatedMemory()
    {
        var storeMock = new Mock<IRouterMemoryStore>();
        var memory = new RouterMemory(storeMock.Object);

        await memory.AddScoreAsync("bug_fix", "model-b", 0.95);

        storeMock.Verify(s => s.SaveAsync(It.Is<ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>>(
            d => d.ContainsKey("bug_fix") &&
                 d["bug_fix"].ContainsKey("model-b") &&
                 d["bug_fix"]["model-b"].Count == 1)), Times.Once);
    }

    [Fact]
    public async Task Persistence_WithSharedStore_SurvivesMemoryRecreation()
    {
        var store = new VectorStoreRouterMemoryStore();
        var firstMemory = new RouterMemory(store);
        await firstMemory.AddScoreAsync("refactor", "model-c", 0.8);
        await firstMemory.AddScoreAsync("refactor", "model-c", 1.0);

        var secondMemory = new RouterMemory(store);
        await secondMemory.InitializeAsync();

        var average = secondMemory.GetAverageScore("refactor", "model-c");
        Assert.NotNull(average);
        Assert.Equal(0.9, average.Value, 3);
    }

    [Fact]
    public async Task AddScoreAsync_WithConcurrentCalls_StoresAllScores()
    {
        var memory = new RouterMemory();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => memory.AddScoreAsync("concurrency", "model-d", i / 100.0));

        await Task.WhenAll(tasks);

        var average = memory.GetAverageScore("concurrency", "model-d");
        Assert.NotNull(average);
    }
}

