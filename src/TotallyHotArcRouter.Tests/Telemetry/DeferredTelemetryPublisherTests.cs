using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Moq;

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
    public async Task PublishSandboxSignalAsync_ForwardsToResolvedInstance()
    {
        var innerMock = new Mock<ITelemetryPublisher>();
        var services = new ServiceCollection();
        services.AddSingleton<ITelemetryPublisher>(_ => innerMock.Object);
        var provider = services.BuildServiceProvider();

        var deferred = new DeferredTelemetryPublisher(provider);
        var signal = new SandboxSignalEvent(
            "corr-1", "sess-1", "live:code_generation", "gpt-5.4", "python", "Tier1Jail",
            true, true, 0, false, 0.87, 42, 1024, DateTimeOffset.UtcNow);
        await deferred.PublishSandboxSignalAsync(signal, TestContext.Current.CancellationToken);

        innerMock.Verify(p => p.PublishSandboxSignalAsync(signal, It.IsAny<CancellationToken>()), Times.Once);
    }
}

