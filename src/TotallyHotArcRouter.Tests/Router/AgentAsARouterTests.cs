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
    private readonly Mock<IRouterModelClient> _modelClientMock;
    private readonly Mock<IOptions<RoutingOptions>> _optionsMock;
    private readonly RouterMemory _memory;
    private readonly RoutingOptions _routingOptions;

    public AgentAsARouterTests()
    {
        _loggerMock = new Mock<ILogger<AgentAsARouter>>();
        _modelClientMock = new Mock<IRouterModelClient>();
        _optionsMock = new Mock<IOptions<RoutingOptions>>();
        _memory = new RouterMemory();
        _routingOptions = new RoutingOptions();
        _optionsMock.Setup(o => o.Value).Returns(_routingOptions);
    }

    [Fact]
    public async Task RouteAsync_Exploitation_SelectsBestModel()
    {
        // Arrange
        // Force pure exploitation: the default RoutingOptions has a nonzero ExplorationRate, which
        // would otherwise make this test flaky (RouteAsync randomly diverts to ExploreAsync ~5% of
        // runs and returns an unrelated random model instead of exercising the exploitation path).
        _optionsMock.Setup(o => o.Value).Returns(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 });

        var dimension = "test_dimension";
        await _memory.AddScoreAsync(dimension, "model1", 0.7);
        await _memory.AddScoreAsync(dimension, "model2", 0.9);
        _modelClientMock.Setup(c => c.GetResponseAsync("model2", "prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("response from model2");

        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _modelClientMock.Object, _memory);

        // Act
        var result = await router.RouteAsync("prompt", dimension, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("model2", result.Decision.SelectedModel);
        Assert.Contains("Selected best model", result.Decision.Rationale);
        _modelClientMock.Verify(c => c.GetResponseAsync("model2", "prompt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RouteAsync_Exploitation_HandlesModelFailure()
    {
        // Arrange
        // Force pure exploitation - see RouteAsync_Exploitation_SelectsBestModel's matching comment.
        _optionsMock.Setup(o => o.Value).Returns(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 });

        var dimension = "test_dimension";
        await _memory.AddScoreAsync(dimension, "model1", 0.8);
        await _memory.AddScoreAsync(dimension, "model2", 0.9);
        _modelClientMock.Setup(c => c.GetResponseAsync("model2", "prompt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Model failed"));
        _modelClientMock.Setup(c => c.GetResponseAsync("model1", "prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("response from model1");

        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _modelClientMock.Object, _memory);

        // Act
        var result = await router.RouteAsync("prompt", dimension, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("model1", result.Decision.SelectedModel);
        _modelClientMock.Verify(c => c.GetResponseAsync("model2", "prompt", It.IsAny<CancellationToken>()), Times.Once);
        _modelClientMock.Verify(c => c.GetResponseAsync("model1", "prompt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RouteAsync_Exploration_SelectsRandomModel()
    {
        // Arrange
        var options = new RoutingOptions { EnableExploration = true, ExplorationRate = 1.0 }; // Force exploration
        _optionsMock.Setup(o => o.Value).Returns(options);
        _modelClientMock.Setup(c => c.GetResponseAsync(It.IsAny<string>(), "prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("response");

        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _modelClientMock.Object, _memory);

        // Act
        var result = await router.RouteAsync("prompt", "test_dimension", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(result.Decision.SelectedModel, RouterConstants.SupportedModels);
        Assert.Equal("Exploration: randomly selected model to gather new data.", result.Decision.Rationale);
    }

    [Fact]
    public async Task Observe_AddsScoreToMemory()
    {
        // Arrange
        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _modelClientMock.Object, _memory);
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
        var router = new AgentAsARouter(_loggerMock.Object, _optionsMock.Object, _modelClientMock.Object, _memory);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => router.ObserveAsync("dim", "mod", -0.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => router.ObserveAsync("dim", "mod", 1.1));
    }
}

