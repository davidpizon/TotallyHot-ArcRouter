using System.Globalization;
using System.Threading.Channels;
using TotallyHot.ArcRouter.Telemetry;
using Moq;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="TelemetryBroadcaster"/>: the gRPC-era fan-out registry replacing SignalR's <c>IHubContext.Clients.All</c>.</summary>
public class TelemetryBroadcasterTests
{
    private static RoutingTelemetryEvent SampleEvent(
        int? promptTokens = 100,
        int? completionTokens = 20,
        decimal? estimatedCostUsd = 0.001m,
        string? requestSummary = "What's the weather?",
        string? responseSummary = "It's sunny.",
        int? cacheCreationTokens = 30,
        int? cacheReadTokens = 500,
        int routerTokens = 64,
        decimal routerCostUsd = 0.000003456m) => new(
        SessionId: "sess-1",
        TurnNumber: 3,
        IsSessionSynthesized: false,
        RequestedModel: "gpt-5.4",
        ResolvedModel: "gpt-5.4-mini",
        Provider: "openai",
        IsFallback: true,
        PromptTokens: promptTokens,
        CompletionTokens: completionTokens,
        EstimatedCostUsd: estimatedCostUsd,
        IsStreaming: true,
        LatencyToHeadersMs: 250,
        TotalDurationMs: 800,
        StatusCode: 200,
        TimestampUtc: new DateTimeOffset(2026, 7, 9, 15, 0, 0, TimeSpan.Zero),
        CacheCreationTokens: cacheCreationTokens,
        CacheReadTokens: cacheReadTokens,
        RequestSummary: requestSummary,
        ResponseSummary: responseSummary,
        RouterTokens: routerTokens,
        RouterCostUsd: routerCostUsd);

    private static SandboxSignalEvent SampleSignal(int? exitCode = 0) => new(
        CorrelationId: "corr-1",
        SessionId: "sess-1",
        Dimension: "live:code_generation",
        Model: "gpt-5.4",
        Language: "python",
        Tier: "Tier1Jail",
        SyntaxValid: true,
        Executed: true,
        ExitCode: exitCode,
        TimedOut: false,
        UnifiedScore: 0.87,
        WallClockMs: 42,
        PeakMemoryBytes: 1024,
        TimestampUtc: new DateTimeOffset(2026, 7, 9, 15, 0, 0, TimeSpan.Zero));

    private static async Task<Contract.TelemetryEvent> ReadOneAsync(ChannelReader<Contract.TelemetryEvent> reader)
    {
        var read = await reader.WaitToReadAsync();
        Assert.True(read);
        Assert.True(reader.TryRead(out var item));
        return item!;
    }

    [Fact]
    public void Publish_NoRegisteredWriters_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();

        broadcaster.Publish(SampleEvent());
    }

    [Fact]
    public void PublishLogLine_NoRegisteredWriters_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();

        broadcaster.PublishLogLine(new LogLineEvent(DateTimeOffset.UtcNow, "INFO", "hi"));
    }

    [Fact]
    public async Task Publish_WritesRoutingTelemetryEnvelopeWithAllFieldsMapped()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        var telemetryEvent = SampleEvent();
        broadcaster.Publish(telemetryEvent);

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, envelope.EventCase);

        var wire = envelope.RoutingTelemetry;
        Assert.Equal(telemetryEvent.SessionId, wire.SessionId);
        Assert.Equal(telemetryEvent.TurnNumber, wire.TurnNumber);
        Assert.Equal(telemetryEvent.IsSessionSynthesized, wire.IsSessionSynthesized);
        Assert.Equal(telemetryEvent.RequestedModel, wire.RequestedModel);
        Assert.Equal(telemetryEvent.ResolvedModel, wire.ResolvedModel);
        Assert.Equal(telemetryEvent.Provider, wire.Provider);
        Assert.Equal(telemetryEvent.IsFallback, wire.IsFallback);
        Assert.True(wire.HasPromptTokens);
        Assert.Equal(telemetryEvent.PromptTokens, wire.PromptTokens);
        Assert.True(wire.HasCompletionTokens);
        Assert.Equal(telemetryEvent.CompletionTokens, wire.CompletionTokens);
        Assert.True(wire.HasCacheCreationTokens);
        Assert.Equal(telemetryEvent.CacheCreationTokens, wire.CacheCreationTokens);
        Assert.True(wire.HasCacheReadTokens);
        Assert.Equal(telemetryEvent.CacheReadTokens, wire.CacheReadTokens);
        Assert.True(wire.HasEstimatedCostUsd);
        Assert.Equal(telemetryEvent.EstimatedCostUsd, decimal.Parse(wire.EstimatedCostUsd, CultureInfo.InvariantCulture));
        Assert.Equal(telemetryEvent.IsStreaming, wire.IsStreaming);
        Assert.Equal(telemetryEvent.LatencyToHeadersMs, wire.LatencyToHeadersMs);
        Assert.Equal(telemetryEvent.TotalDurationMs, wire.TotalDurationMs);
        Assert.Equal(telemetryEvent.StatusCode, wire.StatusCode);
        Assert.Equal(telemetryEvent.TimestampUtc, wire.TimestampUtc.ToDateTimeOffset());
        Assert.True(wire.HasRequestSummary);
        Assert.Equal(telemetryEvent.RequestSummary, wire.RequestSummary);
        Assert.True(wire.HasResponseSummary);
        Assert.Equal(telemetryEvent.ResponseSummary, wire.ResponseSummary);
        Assert.True(wire.HasCostConfidence);
        Assert.Equal(telemetryEvent.CostConfidence.ToString(), wire.CostConfidence);
        Assert.True(wire.HasRouterTokens);
        Assert.Equal(telemetryEvent.RouterTokens, wire.RouterTokens);
        Assert.True(wire.HasRouterCostUsd);
        Assert.Equal(telemetryEvent.RouterCostUsd, decimal.Parse(wire.RouterCostUsd, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Publish_ZeroRouterOverhead_IsStatedOnTheWireRatherThanOmitted()
    {
        // Zero router tokens is a measurement ("the router spent nothing on this request"), not an absent
        // value, so it must arrive as a set field. If it were omitted, a receiver could not tell it apart
        // from an older proxy that never reported router cost - and the difference matters, because one
        // means net savings equal gross and the other means net savings are unknown.
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        broadcaster.Publish(SampleEvent(routerTokens: 0, routerCostUsd: 0m));

        var wire = (await ReadOneAsync(channel.Reader)).RoutingTelemetry;
        Assert.True(wire.HasRouterTokens);
        Assert.Equal(0, wire.RouterTokens);
        Assert.True(wire.HasRouterCostUsd);
        Assert.Equal(0m, decimal.Parse(wire.RouterCostUsd, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Publish_NullOptionalFields_NotSetOnWireMessage()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        broadcaster.Publish(SampleEvent(
            promptTokens: null,
            completionTokens: null,
            estimatedCostUsd: null,
            requestSummary: null,
            responseSummary: null,
            cacheCreationTokens: null,
            cacheReadTokens: null));

        var envelope = await ReadOneAsync(channel.Reader);
        var wire = envelope.RoutingTelemetry;

        Assert.False(wire.HasPromptTokens);
        Assert.False(wire.HasCompletionTokens);
        Assert.False(wire.HasCacheCreationTokens);
        Assert.False(wire.HasCacheReadTokens);
        Assert.False(wire.HasEstimatedCostUsd);
        Assert.False(wire.HasRequestSummary);
        Assert.False(wire.HasResponseSummary);

        // CostConfidence is a non-nullable enum on the source event (defaults to Unknown), so
        // TelemetryBroadcaster.ToWire always sets it - unlike the fields above, its absence here isn't
        // a "not set" case to assert.
        Assert.True(wire.HasCostConfidence);
        Assert.Equal(CostConfidence.Unknown.ToString(), wire.CostConfidence);
    }

    [Fact]
    public async Task PublishLogLine_WritesLogLineEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        var timestamp = new DateTimeOffset(2026, 7, 9, 15, 0, 0, TimeSpan.Zero);
        broadcaster.PublishLogLine(new LogLineEvent(timestamp, "ERROR", "Failed to write payload."));

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.LogLine, envelope.EventCase);
        Assert.Equal("ERROR", envelope.LogLine.Level);
        Assert.Equal("Failed to write payload.", envelope.LogLine.Message);
        Assert.Equal(timestamp, envelope.LogLine.TimestampUtc.ToDateTimeOffset());
    }

    [Fact]
    public async Task Publish_WritesToEveryRegisteredWriter()
    {
        var broadcaster = new TelemetryBroadcaster();
        var firstChannel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        var secondChannel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(firstChannel.Writer);
        broadcaster.Register(secondChannel.Writer);

        broadcaster.Publish(SampleEvent());

        var first = await ReadOneAsync(firstChannel.Reader);
        var second = await ReadOneAsync(secondChannel.Reader);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, first.EventCase);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, second.EventCase);
    }

    [Fact]
    public async Task Unregister_StopsReceivingFurtherEvents()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);
        broadcaster.Unregister(channel.Writer);

        broadcaster.Publish(SampleEvent());

        Assert.False(channel.Reader.TryRead(out _));
        await Task.CompletedTask;
    }

    [Fact]
    public void Publish_RegisteredWriterThrows_DoesNotThrow()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>())).Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);

        broadcaster.Publish(SampleEvent());
    }

    [Fact]
    public async Task Publish_OneRegisteredWriterThrows_OtherWritersStillReceiveEvent()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>())).Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);

        var healthyChannel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(healthyChannel.Writer);

        broadcaster.Publish(SampleEvent());

        var envelope = await ReadOneAsync(healthyChannel.Reader);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, envelope.EventCase);
    }

    [Fact]
    public void Register_Null_Throws()
    {
        var broadcaster = new TelemetryBroadcaster();

        Assert.Throws<ArgumentNullException>(() => broadcaster.Register(null!));
    }

    [Fact]
    public void Unregister_Null_Throws()
    {
        var broadcaster = new TelemetryBroadcaster();

        Assert.Throws<ArgumentNullException>(() => broadcaster.Unregister(null!));
    }

    [Fact]
    public void Publish_NullRoutingTelemetryEvent_Throws()
    {
        var broadcaster = new TelemetryBroadcaster();

        // Explicit cast: Publish is now overloaded (RoutingTelemetryEvent / SandboxSignalEvent), so a
        // bare null! is ambiguous between them at compile time.
        Assert.Throws<ArgumentNullException>(() => broadcaster.Publish((RoutingTelemetryEvent)null!));
    }

    [Fact]
    public void PublishLogLine_NullEvent_Throws()
    {
        var broadcaster = new TelemetryBroadcaster();

        Assert.Throws<ArgumentNullException>(() => broadcaster.PublishLogLine(null!));
    }

    [Fact]
    public async Task Publish_WritesSandboxSignalEnvelopeWithAllFieldsMapped()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        var signal = SampleSignal(exitCode: 1);
        broadcaster.Publish(signal);

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.SandboxSignal, envelope.EventCase);

        var wire = envelope.SandboxSignal;
        Assert.Equal(signal.CorrelationId, wire.CorrelationId);
        Assert.Equal(signal.SessionId, wire.SessionId);
        Assert.Equal(signal.Dimension, wire.Dimension);
        Assert.Equal(signal.Model, wire.Model);
        Assert.Equal(signal.Language, wire.Language);
        Assert.Equal(signal.Tier, wire.Tier);
        Assert.Equal(signal.SyntaxValid, wire.SyntaxValid);
        Assert.Equal(signal.Executed, wire.Executed);
        Assert.True(wire.HasExitCode);
        Assert.Equal(signal.ExitCode, wire.ExitCode);
        Assert.Equal(signal.TimedOut, wire.TimedOut);
        Assert.Equal(signal.UnifiedScore, wire.UnifiedScore);
        Assert.Equal(signal.WallClockMs, wire.WallClockMs);
        Assert.Equal(signal.PeakMemoryBytes, wire.PeakMemoryBytes);
        Assert.Equal(signal.TimestampUtc, wire.TimestampUtc.ToDateTimeOffset());
    }

    [Fact]
    public async Task Publish_SandboxSignalWithNullExitCode_NotSetOnWireMessage()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        broadcaster.Publish(SampleSignal(exitCode: null));

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.False(envelope.SandboxSignal.HasExitCode);
    }

    [Fact]
    public void Publish_NullSandboxSignal_Throws()
    {
        var broadcaster = new TelemetryBroadcaster();

        Assert.Throws<ArgumentNullException>(() => broadcaster.Publish((SandboxSignalEvent)null!));
    }
}

