using Moq;
using System.Threading.Channels;
using TotallyHot.ArcRouter.Telemetry;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryPublisher"/>: the <see cref="ITelemetryPublisher"/> wrapper around
/// <see cref="TelemetryBroadcaster"/>.
/// </summary>
public class TelemetryPublisherTests
{
    private static RoutingTelemetryEvent SampleEvent()
    {
        return new RoutingTelemetryEvent(
            SessionId: "sess-1",
            1,
            false,
            RequestedModel: "gpt-5.4",
            ResolvedModel: "gpt-5.4",
            Provider: "openai",
            false,
            100,
            20,
            0.001m,
            false,
            250,
            800,
            200,
            TimestampUtc: DateTimeOffset.UtcNow,
            RoutedModel: "gpt-5.4");
    }

    [Fact]
    public void Constructor_NullBroadcaster_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryPublisher(null!));
    }

    [Fact]
    public async Task PublishAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishAsync(telemetryEvent: SampleEvent(),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishAsync_RegisteredWriter_ForwardsRoutingTelemetryEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var telemetryEvent = SampleEvent();
        await publisher.PublishAsync(telemetryEvent: telemetryEvent,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, actual: envelope.EventCase);
        Assert.Equal(expected: telemetryEvent.SessionId, actual: envelope.RoutingTelemetry.SessionId);
    }

    [Fact]
    public async Task PublishAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>()))
            .Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        // Must not throw: telemetry publishing failures must never affect the caller (the proxy's
        // request-handling path).
        await publisher.PublishAsync(telemetryEvent: SampleEvent(),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishLogLineAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishLogLineAsync(
            logLine: new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "INFO", Message: "hi"),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishLogLineAsync_RegisteredWriter_ForwardsLogLineEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var logLine = new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "ERROR",
            Message: "Failed to write payload.");
        await publisher.PublishLogLineAsync(logLine: logLine, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.LogLine, actual: envelope.EventCase);
        Assert.Equal(expected: logLine.Message, actual: envelope.LogLine.Message);
    }

    [Fact]
    public async Task PublishLogLineAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>()))
            .Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        await publisher.PublishLogLineAsync(
            logLine: new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "ERROR", Message: "boom"),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static QualitySignalEvent SampleSignal()
    {
        return new QualitySignalEvent(
            CorrelationId: "corr-1",
            SessionId: "sess-1",
            Dimension: "live:code_generation",
            Model: "gpt-5.4",
            Language: "CSharp",
            true,
            true,
            0.9,
            0.8,
            0.87,
            null,
            TimestampUtc: DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PublishQualitySignalAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishQualitySignalAsync(signal: SampleSignal(),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishQualitySignalAsync_RegisteredWriter_ForwardsQualitySignalEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var signal = SampleSignal();
        await publisher.PublishQualitySignalAsync(signal: signal,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.QualitySignal, actual: envelope.EventCase);
        Assert.Equal(expected: signal.CorrelationId, actual: envelope.QualitySignal.CorrelationId);
        Assert.Equal(expected: signal.Model, actual: envelope.QualitySignal.Model);
        Assert.Equal(expected: signal.UnifiedScore, actual: envelope.QualitySignal.UnifiedScore);
    }

    [Fact]
    public async Task PublishQualitySignalAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>()))
            .Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        await publisher.PublishQualitySignalAsync(signal: SampleSignal(),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}