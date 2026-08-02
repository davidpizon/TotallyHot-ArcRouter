using System.Threading.Channels;
using TotallyHot.ArcRouter.Telemetry;
using Moq;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="TelemetryPublisher"/>: the <see cref="ITelemetryPublisher"/> wrapper around <see cref="TelemetryBroadcaster"/>.</summary>
public class TelemetryPublisherTests
{
    private static RoutingTelemetryEvent SampleEvent() => new(
        SessionId: "sess-1",
        TurnNumber: 1,
        IsSessionSynthesized: false,
        RequestedModel: "gpt-5.4",
        ResolvedModel: "gpt-5.4",
        Provider: "openai",
        IsFallback: false,
        PromptTokens: 100,
        CompletionTokens: 20,
        EstimatedCostUsd: 0.001m,
        IsStreaming: false,
        LatencyToHeadersMs: 250,
        TotalDurationMs: 800,
        StatusCode: 200,
        TimestampUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Constructor_NullBroadcaster_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryPublisher(null!));
    }

    [Fact]
    public async Task PublishAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishAsync(SampleEvent(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishAsync_RegisteredWriter_ForwardsRoutingTelemetryEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var telemetryEvent = SampleEvent();
        await publisher.PublishAsync(telemetryEvent, TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, envelope!.EventCase);
        Assert.Equal(telemetryEvent.SessionId, envelope.RoutingTelemetry.SessionId);
    }

    [Fact]
    public async Task PublishAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>())).Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        // Must not throw: telemetry publishing failures must never affect the caller (the proxy's
        // request-handling path).
        await publisher.PublishAsync(SampleEvent(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishLogLineAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishLogLineAsync(new LogLineEvent(DateTimeOffset.UtcNow, "INFO", "hi"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishLogLineAsync_RegisteredWriter_ForwardsLogLineEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var logLine = new LogLineEvent(DateTimeOffset.UtcNow, "ERROR", "Failed to write payload.");
        await publisher.PublishLogLineAsync(logLine, TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.LogLine, envelope!.EventCase);
        Assert.Equal(logLine.Message, envelope.LogLine.Message);
    }

    [Fact]
    public async Task PublishLogLineAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>())).Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        await publisher.PublishLogLineAsync(new LogLineEvent(DateTimeOffset.UtcNow, "ERROR", "boom"), TestContext.Current.CancellationToken);
    }

    private static SandboxSignalEvent SampleSignal() => new(
        CorrelationId: "corr-1",
        SessionId: "sess-1",
        Dimension: "live:code_generation",
        Model: "gpt-5.4",
        Language: "python",
        Tier: "Tier1Jail",
        SyntaxValid: true,
        Executed: true,
        ExitCode: 0,
        TimedOut: false,
        UnifiedScore: 0.87,
        WallClockMs: 42,
        PeakMemoryBytes: 1024,
        TimestampUtc: DateTimeOffset.UtcNow);

    [Fact]
    public async Task PublishSandboxSignalAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishSandboxSignalAsync(SampleSignal(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishSandboxSignalAsync_RegisteredWriter_ForwardsSandboxSignalEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var signal = SampleSignal();
        await publisher.PublishSandboxSignalAsync(signal, TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.SandboxSignal, envelope!.EventCase);
        Assert.Equal(signal.CorrelationId, envelope.SandboxSignal.CorrelationId);
        Assert.Equal(signal.Model, envelope.SandboxSignal.Model);
        Assert.Equal(signal.UnifiedScore, envelope.SandboxSignal.UnifiedScore);
    }

    [Fact]
    public async Task PublishSandboxSignalAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>())).Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        await publisher.PublishSandboxSignalAsync(SampleSignal(), TestContext.Current.CancellationToken);
    }
}

