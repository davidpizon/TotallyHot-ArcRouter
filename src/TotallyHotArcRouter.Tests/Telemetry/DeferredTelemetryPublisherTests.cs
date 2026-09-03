using Microsoft.Extensions.DependencyInjection;
using Moq;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="DeferredTelemetryPublisher"/>: the lazy-resolution wrapper that avoids a circular DI dependency.</summary>
public class DeferredTelemetryPublisherTests
{
    [Fact]
    public void Constructor_NullServiceProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeferredTelemetryPublisher(null!));
    }

    [Fact]
    public async Task PublishAsync_DoesNotResolveUntilFirstCall()
    {
        var innerMock = new Mock<ITelemetryPublisher>();
        var services = new ServiceCollection();
        var resolveCount = 0;
        services.AddSingleton<ITelemetryPublisher>(_ =>
        {
            resolveCount++;
            return innerMock.Object;
        });
        var provider = services.BuildServiceProvider();

        var deferred = new DeferredTelemetryPublisher(provider);
        Assert.Equal(0, resolveCount);

        var telemetryEvent = new RoutingTelemetryEvent(
            "sess-1", 1, false, "gpt-5.4", "gpt-5.4", "openai", false,
            100, 20, 0.001m, false, 250, 800, 200, DateTimeOffset.UtcNow, "gpt-5.4");
        await deferred.PublishAsync(telemetryEvent, TestContext.Current.CancellationToken);

        Assert.Equal(1, resolveCount);
        innerMock.Verify(p => p.PublishAsync(telemetryEvent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishLogLineAsync_ResolvesOnceAndReusesSameInstance()
    {
        var innerMock = new Mock<ITelemetryPublisher>();
        var services = new ServiceCollection();
        var resolveCount = 0;
        services.AddSingleton<ITelemetryPublisher>(_ =>
        {
            resolveCount++;
            return innerMock.Object;
        });
        var provider = services.BuildServiceProvider();

        var deferred = new DeferredTelemetryPublisher(provider);
        var logLine = new LogLineEvent(DateTimeOffset.UtcNow, "INFO", "first");
        await deferred.PublishLogLineAsync(logLine, TestContext.Current.CancellationToken);
        await deferred.PublishLogLineAsync(logLine with { Message = "second" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, resolveCount);
        innerMock.Verify(p => p.PublishLogLineAsync(It.IsAny<LogLineEvent>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PublishQualitySignalAsync_ForwardsToResolvedInstance()
    {
        var innerMock = new Mock<ITelemetryPublisher>();
        var services = new ServiceCollection();
        services.AddSingleton<ITelemetryPublisher>(_ => innerMock.Object);
        var provider = services.BuildServiceProvider();

        var deferred = new DeferredTelemetryPublisher(provider);
        var signal = new QualitySignalEvent(
            "corr-1", "sess-1", "live:code_generation", "gpt-5.4", "CSharp",
            true, true, 0.9, 0.8, 0.87, null, DateTimeOffset.UtcNow);
        await deferred.PublishQualitySignalAsync(signal, TestContext.Current.CancellationToken);

        innerMock.Verify(p => p.PublishQualitySignalAsync(signal, It.IsAny<CancellationToken>()), Times.Once);
    }
}

