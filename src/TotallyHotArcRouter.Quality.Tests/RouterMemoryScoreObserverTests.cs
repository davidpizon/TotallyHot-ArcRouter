using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the host adapter that writes quality scores into router memory under a live namespace.</summary>
public class RouterMemoryScoreObserverTests
{
    private static RouterMemoryScoreObserver CreateObserver(
        RouterMemory memory,
        QualityOptions? options = null,
        ITelemetryPublisher? publisher = null)
    {
        return new RouterMemoryScoreObserver(memory: memory, options: Options.Create(options ?? new QualityOptions()),
            logger: NullLogger<RouterMemoryScoreObserver>.Instance, telemetryPublisher: publisher);
    }

    [Fact]
    public async Task ObserveAsync_WritesUnderLivePrefix()
    {
        var memory = new RouterMemory();
        var observer = CreateObserver(memory);
        var result = new QualityResult
        {
            Dimension = "code_generation",
            Model = "gpt-5.4",
            UnifiedScore = 0.75
        };

        await observer.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.75, actual: memory.GetAverageScore(dimension: "live:code_generation", model: "gpt-5.4"));
        Assert.Null(memory.GetAverageScore(dimension: "code_generation", model: "gpt-5.4"));
    }

    [Fact]
    public async Task ObserveAsync_ClampsScoreIntoUnitInterval()
    {
        var memory = new RouterMemory();
        var observer = CreateObserver(memory);

        await observer.ObserveAsync(result: new QualityResult { Dimension = "d", Model = "m", UnifiedScore = 1.5 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.0, actual: memory.GetAverageScore(dimension: "live:d", model: "m"));
    }

    [Fact]
    public async Task ObserveAsync_NoModel_SkipsObservation()
    {
        var memory = new RouterMemory();
        var observer = CreateObserver(memory);

        await observer.ObserveAsync(
            result: new QualityResult { Dimension = "d", Model = string.Empty, UnifiedScore = 0.5 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(memory.GetModelsForDimension("live:d"));
    }

    [Fact]
    public async Task ObserveAsync_PublishesQualitySignalWithCorrelationId()
    {
        var memory = new RouterMemory();
        var publisher = new Mock<ITelemetryPublisher>();
        publisher
            .Setup(p => p.PublishQualitySignalAsync(It.IsAny<QualitySignalEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var observer = CreateObserver(memory: memory, publisher: publisher.Object);

        await observer.ObserveAsync(result: new QualityResult
        {
            Dimension = "code_generation",
            Model = "gpt-5.4",
            UnifiedScore = 0.9,
            RequestCorrelationId = "sess-1:3"
        }, cancellationToken: TestContext.Current.CancellationToken);

        publisher.Verify(
            expression: p => p.PublishQualitySignalAsync(
                It.Is<QualitySignalEvent>(s => s.CorrelationId == "sess-1:3" && s.Dimension == "live:code_generation"),
                It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    [Fact]
    public async Task ObserveAsync_EmptyCorrelationId_ObservesButDoesNotPublish()
    {
        var memory = new RouterMemory();
        var publisher = new Mock<ITelemetryPublisher>();
        var observer = CreateObserver(memory: memory, publisher: publisher.Object);

        await observer.ObserveAsync(result: new QualityResult
        {
            Dimension = "code_generation",
            Model = "gpt-5.4",
            UnifiedScore = 0.9,
            RequestCorrelationId = string.Empty
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The score is still learned, but an unjoinable signal is not published.
        Assert.Equal(0.9, actual: memory.GetAverageScore(dimension: "live:code_generation", model: "gpt-5.4"));
        publisher.Verify(
            expression: p => p.PublishQualitySignalAsync(It.IsAny<QualitySignalEvent>(), It.IsAny<CancellationToken>()),
            times: Times.Never);
    }
}