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
        ITelemetryPublisher? publisher = null) =>
        new(memory, Options.Create(options ?? new QualityOptions()), NullLogger<RouterMemoryScoreObserver>.Instance, publisher);

    [Fact]
    public async Task ObserveAsync_WritesUnderLivePrefix()
    {
        var memory = new RouterMemory();
        var observer = CreateObserver(memory);
        var result = new QualityResult
        {
            Dimension = "code_generation",
            Model = "gpt-5.4",
            UnifiedScore = 0.75,
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal(0.75, memory.GetAverageScore("live:code_generation", "gpt-5.4"));
        Assert.Null(memory.GetAverageScore("code_generation", "gpt-5.4"));
    }

    [Fact]
    public async Task ObserveAsync_ClampsScoreIntoUnitInterval()
    {
        var memory = new RouterMemory();
        var observer = CreateObserver(memory);

        await observer.ObserveAsync(new QualityResult { Dimension = "d", Model = "m", UnifiedScore = 1.5 }, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, memory.GetAverageScore("live:d", "m"));
    }

    [Fact]
    public async Task ObserveAsync_NoModel_SkipsObservation()
    {
        var memory = new RouterMemory();
        var observer = CreateObserver(memory);

        await observer.ObserveAsync(new QualityResult { Dimension = "d", Model = string.Empty, UnifiedScore = 0.5 }, TestContext.Current.CancellationToken);

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
        var observer = CreateObserver(memory, publisher: publisher.Object);

        await observer.ObserveAsync(new QualityResult
        {
            Dimension = "code_generation",
            Model = "gpt-5.4",
            UnifiedScore = 0.9,
            RequestCorrelationId = "sess-1:3",
        }, TestContext.Current.CancellationToken);

        publisher.Verify(
            p => p.PublishQualitySignalAsync(
                It.Is<QualitySignalEvent>(s => s.CorrelationId == "sess-1:3" && s.Dimension == "live:code_generation"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ObserveAsync_EmptyCorrelationId_ObservesButDoesNotPublish()
    {
        var memory = new RouterMemory();
        var publisher = new Mock<ITelemetryPublisher>();
        var observer = CreateObserver(memory, publisher: publisher.Object);

        await observer.ObserveAsync(new QualityResult
        {
            Dimension = "code_generation",
            Model = "gpt-5.4",
            UnifiedScore = 0.9,
            RequestCorrelationId = string.Empty,
        }, TestContext.Current.CancellationToken);

        // The score is still learned, but an unjoinable signal is not published.
        Assert.Equal(0.9, memory.GetAverageScore("live:code_generation", "gpt-5.4"));
        publisher.Verify(
            p => p.PublishQualitySignalAsync(It.IsAny<QualitySignalEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

