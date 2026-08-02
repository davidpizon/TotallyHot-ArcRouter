using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Covers regression behavior for router memory persistence across process-like restarts.
/// </summary>
[Collection("Integration")]
public sealed class PersistenceRegressionTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _memoryFilePath;

    public PersistenceRegressionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"router_persistence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _memoryFilePath = Path.Combine(_tempDirectory, "router_memory.json");
    }

    [Fact]
    public async Task JsonMemoryStore_PersistsScores_AcrossMemoryRecreation()
    {
        var options = Options.Create(new RoutingOptions
        {
            MemoryPath = _memoryFilePath
        });

        var store = new JsonRouterMemoryStore(new NullLogger<JsonRouterMemoryStore>(), options);

        var firstMemory = new RouterMemory(store, new NullLogger<RouterMemory>());
        await firstMemory.AddScoreAsync("bug_fix", "gpt-5.4", 0.8);
        await firstMemory.AddScoreAsync("bug_fix", "gpt-5.4", 1.0);

        var secondMemory = new RouterMemory(store, new NullLogger<RouterMemory>());
        await secondMemory.InitializeAsync();

        var average = secondMemory.GetAverageScore("bug_fix", "gpt-5.4");
        Assert.NotNull(average);
        Assert.Equal(0.9, average.Value, 3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

