using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Tests for the <see cref="AgentAsARouter"/> class.
/// </summary>
public class AgentAsARouterTests
{
    private readonly Mock<ILogger<AgentAsARouter>> _loggerMock;
    private readonly Mock<IOptions<RoutingOptions>> _optionsMock;
    private readonly RouterMemory _memory;
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
        await _memory.AddScoreAsync(dimension, "model1", 0.7);
        await _memory.AddScoreAsync(dimension, "model2", 0.9);

        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _memory);

        // Act
        var decision = await router.SelectModelAsync(dimension, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("model2", decision.SelectedModel);
        Assert.Contains("Selected best model", decision.Rationale);
    }

    [Fact]
    public async Task SelectModelAsync_Exploration_SelectsRandomModel()
    {
        // Arrange
        var options = new RoutingOptions { EnableExploration = true, ExplorationRate = 1.0 }; // Force exploration
        _optionsMock.Setup(o => o.Value).Returns(options);

        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _memory);

        // Act
        var decision = await router.SelectModelAsync("test_dimension", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(decision.SelectedModel, RouterConstants.SupportedModels);
        Assert.Equal("Exploration: randomly selected model to gather new data.", decision.Rationale);
    }

    [Fact]
    public async Task SelectModelAsync_NoHistory_FallsBackToDefaultModel()
    {
        // Arrange
        _optionsMock.Setup(o => o.Value).Returns(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 });
        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _memory);

        // Act
        var decision = await router.SelectModelAsync("unknown_dimension", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(_routingOptions.DefaultModel, decision.SelectedModel);
        Assert.Equal(RouterConstants.FallbackReason, decision.Rationale);
    }

    [Fact]
    public async Task Observe_AddsScoreToMemory()
    {
        // Arrange
        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _memory);
        var dimension = "test_dimension";
        var model = "test_model";
        var score = 0.95;

        // Act
        await router.ObserveAsync(dimension, model, score);
        var averageScore = _memory.GetAverageScore(dimension, model);

        // Assert
        Assert.Equal(score, averageScore);
    }

    [Fact]
    public async Task Observe_Throws_OnInvalidScore()
    {
        // Arrange
        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _memory);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => router.ObserveAsync("dim", "mod", -0.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => router.ObserveAsync("dim", "mod", 1.1));
    }
}
