using TotallyHot.ArcRouter.Router;
using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Tests for <see cref="VectorStoreRouterMemoryStore"/>.
/// </summary>
public class VectorStoreRouterMemoryStoreTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsMemory()
    {
        var store = new VectorStoreRouterMemoryStore();
        var input = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>
        {
            ["code_generation"] = new ConcurrentDictionary<string, List<double>>
            {
                ["gpt-5.4"] = [0.8, 0.9]
            }
        };

        await store.SaveAsync(input);
        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded["code_generation"]["gpt-5.4"].Count);
    }

    [Fact]
    public async Task FindSimilarAsync_WithEmptyStore_ReturnsEmpty()
    {
        var store = new VectorStoreRouterMemoryStore();

        var result = await store.FindSimilarAsync("refactor python code", topK: 3, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilarAsync_ReturnsTopMatchingModels()
    {
        var store = new VectorStoreRouterMemoryStore();
        var input = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>
        {
            ["python_refactor"] = new ConcurrentDictionary<string, List<double>>
            {
                ["model-a"] = [0.9, 1.0]
            },
            ["frontend_css"] = new ConcurrentDictionary<string, List<double>>
            {
                ["model-b"] = [0.95]
            }
        };

        await store.SaveAsync(input);

        var result = await store.FindSimilarAsync("python refactor task", topK: 2, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result);
        Assert.Equal("model-a", result[0].Model);
    }

    [Fact]
    public async Task SaveAsync_WithConcurrentCalls_KeepsLastSnapshotStable()
    {
        var store = new VectorStoreRouterMemoryStore();

        var tasks = Enumerable.Range(0, 20)
            .Select(i =>
            {
                var snapshot = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>
                {
                    ["dimension"] = new ConcurrentDictionary<string, List<double>>
                    {
                        ["model"] = [i]
                    }
                };

                return store.SaveAsync(snapshot);
            });

        await Task.WhenAll(tasks);
        var loaded = await store.LoadAsync();

        Assert.True(loaded.ContainsKey("dimension"));
        Assert.True(loaded["dimension"].ContainsKey("model"));
        Assert.Single(loaded["dimension"]["model"]);
    }
}

