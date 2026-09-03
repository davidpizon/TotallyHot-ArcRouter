using Moq;
using System.Globalization;
using System.Threading.Channels;
using TotallyHot.ArcRouter.Telemetry;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryBroadcaster"/>: the gRPC-era fan-out registry replacing SignalR's
/// <c>IHubContext.Clients.All</c>.
/// </summary>
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
        decimal routerCostUsd = 0.000003456m)
    {
        return new RoutingTelemetryEvent(
            SessionId: "sess-1",
            3,
            false,
            RequestedModel: "gpt-5.4",
            ResolvedModel: "gpt-5.4-mini",
            Provider: "openai",
            true,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            EstimatedCostUsd: estimatedCostUsd,
            true,
            250,
            800,
            200,
            TimestampUtc: new DateTimeOffset(2026, 7, 9, 15, 0, 0, offset: TimeSpan.Zero),
            RoutedModel: "gpt-5.4-mini",
            CacheCreationTokens: cacheCreationTokens,
            CacheReadTokens: cacheReadTokens,
            RequestSummary: requestSummary,
            ResponseSummary: responseSummary,
            RouterTokens: routerTokens,
            RouterCostUsd: routerCostUsd,
            SubstitutionReason: RoutingSubstitutionReason.Failover);
    }

    private static QualitySignalEvent SampleSignal(double? judgeScore = 0.8)
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
            JudgeScore: judgeScore,
            0.87,
            null,
            TimestampUtc: new DateTimeOffset(2026, 7, 9, 15, 0, 0, offset: TimeSpan.Zero));
    }

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

        broadcaster.PublishLogLine(new LogLineEvent(TimestampUtc: DateTimeOffset.UtcNow, Level: "INFO", Message: "hi"));
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
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, actual: envelope.EventCase);

        var wire = envelope.RoutingTelemetry;
        Assert.Equal(expected: telemetryEvent.SessionId, actual: wire.SessionId);
        Assert.Equal(expected: telemetryEvent.TurnNumber, actual: wire.TurnNumber);
        Assert.Equal(expected: telemetryEvent.IsSessionSynthesized, actual: wire.IsSessionSynthesized);
        Assert.Equal(expected: telemetryEvent.RequestedModel, actual: wire.RequestedModel);
        Assert.Equal(expected: telemetryEvent.ResolvedModel, actual: wire.ResolvedModel);
        Assert.Equal(expected: telemetryEvent.Provider, actual: wire.Provider);
        Assert.Equal(expected: telemetryEvent.IsFallback, actual: wire.IsFallback);
        Assert.True(wire.HasPromptTokens);
        Assert.Equal(expected: telemetryEvent.PromptTokens, actual: wire.PromptTokens);
        Assert.True(wire.HasCompletionTokens);
        Assert.Equal(expected: telemetryEvent.CompletionTokens, actual: wire.CompletionTokens);
        Assert.True(wire.HasCacheCreationTokens);
        Assert.Equal(expected: telemetryEvent.CacheCreationTokens, actual: wire.CacheCreationTokens);
        Assert.True(wire.HasCacheReadTokens);
        Assert.Equal(expected: telemetryEvent.CacheReadTokens, actual: wire.CacheReadTokens);
        Assert.True(wire.HasEstimatedCostUsd);
        Assert.Equal(expected: telemetryEvent.EstimatedCostUsd,
            actual: decimal.Parse(s: wire.EstimatedCostUsd, provider: CultureInfo.InvariantCulture));
        Assert.Equal(expected: telemetryEvent.IsStreaming, actual: wire.IsStreaming);
        Assert.Equal(expected: telemetryEvent.LatencyToHeadersMs, actual: wire.LatencyToHeadersMs);
        Assert.Equal(expected: telemetryEvent.TotalDurationMs, actual: wire.TotalDurationMs);
        Assert.Equal(expected: telemetryEvent.StatusCode, actual: wire.StatusCode);
        Assert.Equal(expected: telemetryEvent.TimestampUtc, actual: wire.TimestampUtc.ToDateTimeOffset());
        Assert.True(wire.HasRequestSummary);
        Assert.Equal(expected: telemetryEvent.RequestSummary, actual: wire.RequestSummary);
        Assert.True(wire.HasResponseSummary);
        Assert.Equal(expected: telemetryEvent.ResponseSummary, actual: wire.ResponseSummary);
        Assert.True(wire.HasCostConfidence);
        Assert.Equal(expected: telemetryEvent.CostConfidence.ToString(), actual: wire.CostConfidence);
        Assert.True(wire.HasRouterTokens);
        Assert.Equal(expected: telemetryEvent.RouterTokens, actual: wire.RouterTokens);
        Assert.True(wire.HasRouterCostUsd);
        Assert.Equal(expected: telemetryEvent.RouterCostUsd,
            actual: decimal.Parse(s: wire.RouterCostUsd, provider: CultureInfo.InvariantCulture));
        Assert.Equal(expected: telemetryEvent.RoutedModel, actual: wire.RoutedModel);
        Assert.Equal(expected: telemetryEvent.SubstitutionReason.ToString(), actual: wire.SubstitutionReason);
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
        Assert.Equal(0, actual: wire.RouterTokens);
        Assert.True(wire.HasRouterCostUsd);
        Assert.Equal(0m, actual: decimal.Parse(s: wire.RouterCostUsd, provider: CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Publish_NullOptionalFields_NotSetOnWireMessage()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        broadcaster.Publish(SampleEvent(
            null,
            null,
            null,
            null,
            null,
            null,
            null));

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
        Assert.Equal(expected: CostConfidence.Unknown.ToString(), actual: wire.CostConfidence);
    }

    [Fact]
    public async Task PublishLogLine_WritesLogLineEnvelope()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        var timestamp = new DateTimeOffset(2026, 7, 9, 15, 0, 0, offset: TimeSpan.Zero);
        broadcaster.PublishLogLine(new LogLineEvent(TimestampUtc: timestamp, Level: "ERROR",
            Message: "Failed to write payload."));

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.LogLine, actual: envelope.EventCase);
        Assert.Equal(expected: "ERROR", actual: envelope.LogLine.Level);
        Assert.Equal(expected: "Failed to write payload.", actual: envelope.LogLine.Message);
        Assert.Equal(expected: timestamp, actual: envelope.LogLine.TimestampUtc.ToDateTimeOffset());
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
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, actual: first.EventCase);
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, actual: second.EventCase);
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
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>()))
            .Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);

        broadcaster.Publish(SampleEvent());
    }

    [Fact]
    public async Task Publish_OneRegisteredWriterThrows_OtherWritersStillReceiveEvent()
    {
        var broadcaster = new TelemetryBroadcaster();
        var writerMock = new Mock<ChannelWriter<Contract.TelemetryEvent>>();
        writerMock.Setup(w => w.TryWrite(It.IsAny<Contract.TelemetryEvent>()))
            .Throws(new InvalidOperationException("boom"));
        broadcaster.Register(writerMock.Object);

        var healthyChannel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(healthyChannel.Writer);

        broadcaster.Publish(SampleEvent());

        var envelope = await ReadOneAsync(healthyChannel.Reader);
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, actual: envelope.EventCase);
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

        // Explicit cast: Publish is now overloaded (RoutingTelemetryEvent / QualitySignalEvent), so a
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
    public async Task Publish_WritesQualitySignalEnvelopeWithAllFieldsMapped()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        var signal = SampleSignal(judgeScore: 0.75);
        broadcaster.Publish(signal);

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.QualitySignal, actual: envelope.EventCase);

        var wire = envelope.QualitySignal;
        Assert.Equal(expected: signal.CorrelationId, actual: wire.CorrelationId);
        Assert.Equal(expected: signal.SessionId, actual: wire.SessionId);
        Assert.Equal(expected: signal.Dimension, actual: wire.Dimension);
        Assert.Equal(expected: signal.Model, actual: wire.Model);
        Assert.Equal(expected: signal.Language, actual: wire.Language);
        Assert.Equal(expected: signal.SyntaxValid, actual: wire.SyntaxValid);
        Assert.Equal(expected: signal.SyntaxAuthoritative, actual: wire.SyntaxAuthoritative);
        Assert.True(wire.HasAnalysisScore);
        Assert.Equal(expected: signal.AnalysisScore, actual: wire.AnalysisScore);
        Assert.True(wire.HasJudgeScore);
        Assert.Equal(expected: signal.JudgeScore, actual: wire.JudgeScore);
        Assert.Equal(expected: signal.UnifiedScore, actual: wire.UnifiedScore);
        Assert.Equal(expected: signal.TimestampUtc, actual: wire.TimestampUtc.ToDateTimeOffset());
    }

    // An unjudged score and a judge score of zero are different facts, and the wire has to keep them
    // apart: leaving the field unset is what lets a reader tell "the judge did not contribute" from "the
    // judge scored this nothing".
    [Fact]
    public async Task Publish_QualitySignalWithNoJudgeScore_LeavesFieldUnsetOnWireMessage()
    {
        var broadcaster = new TelemetryBroadcaster();
        var channel = Channel.CreateUnbounded<Contract.TelemetryEvent>();
        broadcaster.Register(channel.Writer);

        broadcaster.Publish(SampleSignal(judgeScore: null));

        var envelope = await ReadOneAsync(channel.Reader);
        Assert.False(envelope.QualitySignal.HasJudgeScore);
    }

    [Fact]
    public void Publish_NullQualitySignal_Throws()
    {
        var broadcaster = new TelemetryBroadcaster();

        Assert.Throws<ArgumentNullException>(() => broadcaster.Publish((QualitySignalEvent)null!));
    }
}