using Microsoft.Extensions.DependencyInjection;
using Moq;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="DeferredTelemetryPublisher"/>: the lazy-resolution wrapper that avoids a circular DI
/// dependency.
/// </summary>
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
        Assert.Equal(0, actual: resolveCount);

        var telemetryEvent = new RoutingTelemetryEvent(
            SessionId: "sess-1", 1, false, RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4", Provider: "openai",
            false,
            100, 20, 0.001m, false, 250, 800, 200, TimestampUtc: DateTimeOffset.UtcNow, RoutedModel: "gpt-5.4");
        await deferred.PublishAsync(telemetryEvent: telemetryEvent,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: resolveCount);
        innerMock.Verify(expression: p => p.PublishAsync(telemetryEvent, It.IsAny<CancellationToken>()),
            times: Times.Once);
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
        var logLine = new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "INFO", Message: "first");
        await deferred.PublishLogLineAsync(logLine: logLine, cancellationToken: TestContext.Current.CancellationToken);
        await deferred.PublishLogLineAsync(logLine: logLine with { Message = "second" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: resolveCount);
        innerMock.Verify(
            expression: p => p.PublishLogLineAsync(It.IsAny<LogLineEvent>(), It.IsAny<CancellationToken>()),
            times: Times.Exactly(2));
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
            CorrelationId: "corr-1", SessionId: "sess-1", Dimension: "live:code_generation", Model: "gpt-5.4",
            Language: "CSharp",
            true, true, 0.9, 0.8, 0.87, null, TimestampUtc: DateTimeOffset.UtcNow);
        await deferred.PublishQualitySignalAsync(signal: signal,
            cancellationToken: TestContext.Current.CancellationToken);

        innerMock.Verify(expression: p => p.PublishQualitySignalAsync(signal, It.IsAny<CancellationToken>()),
            times: Times.Once);
    }
}