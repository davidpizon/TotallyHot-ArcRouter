using Moq;
using System.Threading.Channels;
using TotallyHot.ArcRouter.Telemetry;
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
        TimestampUtc: DateTimeOffset.UtcNow,
        RoutedModel: "gpt-5.4");

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

    private static QualitySignalEvent SampleSignal() => new(
        CorrelationId: "corr-1",
        SessionId: "sess-1",
        Dimension: "live:code_generation",
        Model: "gpt-5.4",
        Language: "CSharp",
        SyntaxValid: true,
        SyntaxAuthoritative: true,
        AnalysisScore: 0.9,
        JudgeScore: 0.8,
        UnifiedScore: 0.87,
        DegradedReason: null,
        TimestampUtc: DateTimeOffset.UtcNow);

    [Fact]
    public async Task PublishQualitySignalAsync_NoRegisteredWriters_CompletesWithoutThrowing()
    {
        var publisher = new TelemetryPublisher(new TelemetryBroadcaster());

        await publisher.PublishQualitySignalAsync(SampleSignal(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishQualitySignalAsync_RegisteredWriter_ForwardsQualitySignalEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        var publisher = new TelemetryPublisher(broadcaster);

        var signal = SampleSignal();
        await publisher.PublishQualitySignalAsync(signal, TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.QualitySignal, envelope!.EventCase);
        Assert.Equal(signal.CorrelationId, envelope.QualitySignal.CorrelationId);
        Assert.Equal(signal.Model, envelope.QualitySignal.Model);
        Assert.Equal(signal.UnifiedScore, envelope.QualitySignal.UnifiedScore);
    }

    [Fact]
    public async Task PublishQualitySignalAsync_BroadcastFails_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>())).Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);
        var publisher = new TelemetryPublisher(broadcaster);

        await publisher.PublishQualitySignalAsync(SampleSignal(), TestContext.Current.CancellationToken);
    }
}

