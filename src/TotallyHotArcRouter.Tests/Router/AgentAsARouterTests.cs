using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Tests for the <see cref="AgentAsARouter"/> class.
/// </summary>
public class AgentAsARouterTests
{
    private readonly Mock<ILogger<AgentAsARouter>> _loggerMock;
    private readonly RouterMemory _memory;
    private readonly Mock<IOptions<RoutingOptions>> _optionsMock;
    private readonly RoutingOptions _routingOptions;

    public AgentAsARouterTests()
    {
        _loggerMock = new Mock<ILogger<AgentAsARouter>>();
        _optionsMock = new Mock<IOptions<RoutingOptions>>();
        _memory = new RouterMemory();
        _routingOptions = new RoutingOptions();
        _optionsMock.Setup(o => o.Value).Returns(_routingOptions);
    }

    [Fact]
    public async Task SelectModelAsync_Exploitation_SelectsBestModel()
    {
        // Arrange
        // Force pure exploitation: the default RoutingOptions has a nonzero ExplorationRate, which
        // would otherwise make this test flaky (SelectModelAsync randomly diverts to exploration ~5% of
        // runs and returns an unrelated random model instead of exercising the exploitation path).
        _optionsMock.Setup(o => o.Value).Returns(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 });

        var dimension = "test_dimension";
        await _memory.AddScoreAsync(dimension: dimension, model: "model1", 0.7);
        await _memory.AddScoreAsync(dimension: dimension, model: "model2", 0.9);

        var router = new AgentAsARouter(logger: _loggerMock.Object, options: _optionsMock.Object, memory: _memory);

        // Act
        var decision = await router.SelectModelAsync(dimension: dimension,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected: "model2", actual: decision.SelectedModel);
        Assert.Contains(expectedSubstring: "Selected best model", actualString: decision.Rationale);
    }

    [Fact]
    public async Task SelectModelAsync_Exploration_SelectsRandomModel()
    {
        // Arrange
        var options = new RoutingOptions { EnableExploration = true, ExplorationRate = 1.0 }; // Force exploration
        _optionsMock.Setup(o => o.Value).Returns(options);

        var router = new AgentAsARouter(logger: _loggerMock.Object, options: _optionsMock.Object, memory: _memory);

        // Act
        var decision = await router.SelectModelAsync(dimension: "test_dimension",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(expected: decision.SelectedModel, collection: RouterConstants.SupportedModels);
        Assert.Equal(expected: "Exploration: randomly selected model to gather new data.", actual: decision.Rationale);
    }

    [Fact]
    public async Task SelectModelAsync_NoHistory_FallsBackToDefaultModel()
    {
        // Arrange
        _optionsMock.Setup(o => o.Value).Returns(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 });
        var router = new AgentAsARouter(logger: _loggerMock.Object, options: _optionsMock.Object, memory: _memory);

        // Act
        var decision = await router.SelectModelAsync(dimension: "unknown_dimension",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected: _routingOptions.DefaultModel, actual: decision.SelectedModel);
        Assert.Equal(expected: RouterConstants.FallbackReason, actual: decision.Rationale);
    }

    [Fact]
    public async Task Observe_AddsScoreToMemory()
    {
        // Arrange
        var router = new AgentAsARouter(logger: _loggerMock.Object, options: _optionsMock.Object, memory: _memory);
        var dimension = "test_dimension";
        var model = "test_model";
        var score = 0.95;

        // Act
        await router.ObserveAsync(dimension: dimension, model: model, score: score);
        var averageScore = _memory.GetAverageScore(dimension: dimension, model: model);

        // Assert
        Assert.Equal(expected: score, actual: averageScore);
    }

    [Fact]
    public async Task Observe_Throws_OnInvalidScore()
    {
        // Arrange
        var router = new AgentAsARouter(logger: _loggerMock.Object, options: _optionsMock.Object, memory: _memory);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            router.ObserveAsync(dimension: "dim", model: "mod", -0.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            router.ObserveAsync(dimension: "dim", model: "mod", 1.1));
    }
}