using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Tests for the <see cref="JsonRouterMemoryStore"/> class.
/// </summary>
public class JsonRouterMemoryStoreTests : IDisposable
{
    private readonly string _testDirectoryPath;

    public JsonRouterMemoryStoreTests()
    {
        _testDirectoryPath = Path.Combine(Path.GetTempPath(), $"router_memory_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectoryPath);
    }

    [Fact]
    public async Task SaveAndLoad_WorkCorrectly()
    {
        var testFilePath = Path.Combine(_testDirectoryPath, "router_memory.json");
        var loggerMock = new Mock<ILogger<JsonRouterMemoryStore>>();
        var store = CreateStore(loggerMock.Object, testFilePath);

        var memoryToSave = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>();
        memoryToSave["dim1"] = new ConcurrentDictionary<string, List<double>>
        {
            ["model1"] = [0.8, 0.9]
        };

        await store.SaveAsync(memoryToSave);
        var loadedMemory = await store.LoadAsync();

        Assert.True(loadedMemory.ContainsKey("dim1"));
        Assert.True(loadedMemory["dim1"].ContainsKey("model1"));
        Assert.Equal(2, loadedMemory["dim1"]["model1"].Count);

        VerifyLogContains(loggerMock, LogLevel.Information, "Successfully saved memory to");
        VerifyLogContains(loggerMock, LogLevel.Information, "Successfully loaded memory from");
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsEmptyMemory_AndLogsInformation()
    {
        var testFilePath = Path.Combine(_testDirectoryPath, "missing_memory.json");
        var loggerMock = new Mock<ILogger<JsonRouterMemoryStore>>();
        var store = CreateStore(loggerMock.Object, testFilePath);

        var loadedMemory = await store.LoadAsync();

        Assert.Empty(loadedMemory);
        VerifyLogContains(loggerMock, LogLevel.Information, "Memory file not found. Starting with empty memory.");
    }

    [Fact]
    public async Task LoadAsync_WhenJsonIsCorrupted_ReturnsEmptyMemory_AndLogsError()
    {
        var testFilePath = Path.Combine(_testDirectoryPath, "corrupt_memory.json");
        await File.WriteAllTextAsync(testFilePath, "{ this is not valid json }", TestContext.Current.CancellationToken);

        var loggerMock = new Mock<ILogger<JsonRouterMemoryStore>>();
        var store = CreateStore(loggerMock.Object, testFilePath);

        var loadedMemory = await store.LoadAsync();

        Assert.Empty(loadedMemory);
        VerifyLogContains(loggerMock, LogLevel.Error, "Failed to load memory from");
    }

    private static JsonRouterMemoryStore CreateStore(ILogger<JsonRouterMemoryStore> logger, string memoryPath)
    {
        var options = Options.Create(new RoutingOptions
        {
            MemoryPath = memoryPath
        });

        return new JsonRouterMemoryStore(logger, options);
    }

    private static void VerifyLogContains(Mock<ILogger<JsonRouterMemoryStore>> loggerMock, LogLevel logLevel, string expectedText)
    {
        loggerMock.Verify(
            logger => logger.Log(
                logLevel,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(expectedText, StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectoryPath))
        {
            Directory.Delete(_testDirectoryPath, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

