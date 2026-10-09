using Grpc.Core;
using Moq;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryGrpcService"/>'s passkey content gate (ADR-0020): without a valid
/// <c>x-content-grant</c> header, persisted prompt/response text, live request/response summaries, and
/// content-bearing log lines are withheld; with one they flow as before.
/// </summary>
public sealed class TelemetryGrpcServiceContentGateTests
{
    private static TelemetryGrpcService CreateService(
        TelemetryBroadcaster broadcaster,
        ContentGate gate,
        IReadOnlyList<SessionTranscript>? sessions = null)
    {
        var store = new Mock<ITranscriptStore>();
        store.Setup(s => s.ListSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions ?? []);
        return new TelemetryGrpcService(
            broadcaster: broadcaster,
            transcriptStore: store.Object,
            transcriptOptions: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }),
            contentGate: gate);
    }

    private static RoutingTelemetryEvent EventWithSummaries() => new(
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
        RoutedModel: "gpt-5.4",
        RequestSummary: "secret prompt",
        ResponseSummary: "secret reply");

    private static SessionTranscript Transcript() => new(
        1,
        SessionId: "sess-1",
        CorrelationId: "sess-1:1",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        RequestedModel: "gpt-5.4",
        RoutedModel: "kimi-k2.5",
        PromptText: "fix this bug",
        ResponseText: "here is the fix",
        0.0042m,
        100,
        50,
        7,
        PromptTextLength: 12,
        ResponseTextLength: 15);

    private static async Task<List<Contract.TelemetryEvent>> StreamAsync(
        TelemetryGrpcService service,
        TelemetryBroadcaster broadcaster,
        string? grantToken,
        Action publish,
        int expectedCount)
    {
        var writer = new CollectingWriter();
        using var cts = new CancellationTokenSource();
        var call = service.StreamEvents(
            request: new Contract.StreamEventsRequest(),
            responseStream: writer,
            context: PasskeyGateHarness.Context(grantToken, cts.Token));

        publish();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (writer.Written.Count < expectedCount && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        // Settle so an event that must be dropped has had time to (wrongly) arrive.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await call.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return writer.Written;
    }

    [Fact]
    public async Task StreamEvents_WithoutGrant_StripsSummariesFromRoutingEvents()
    {
        var broadcaster = new TelemetryBroadcaster();
        var service = CreateService(broadcaster, PasskeyGateHarness.Create().Gate);

        var written = await StreamAsync(service, broadcaster, grantToken: null,
            publish: () => broadcaster.Publish(EventWithSummaries()), expectedCount: 1);

        var routing = Assert.Single(written).RoutingTelemetry;
        Assert.False(routing.HasRequestSummary);
        Assert.False(routing.HasResponseSummary);
        Assert.Equal("sess-1", routing.SessionId);
    }

    [Fact]
    public async Task StreamEvents_WithGrant_KeepsSummaries()
    {
        var broadcaster = new TelemetryBroadcaster();
        var harness = PasskeyGateHarness.Create();
        var service = CreateService(broadcaster, harness.Gate);

        var written = await StreamAsync(service, broadcaster, grantToken: harness.Grants.IssueGrant(),
            publish: () => broadcaster.Publish(EventWithSummaries()), expectedCount: 1);

        var routing = Assert.Single(written).RoutingTelemetry;
        Assert.Equal("secret prompt", routing.RequestSummary);
        Assert.Equal("secret reply", routing.ResponseSummary);
    }

    [Fact]
    public async Task StreamEvents_WithoutGrant_SkipsContentBearingLogLinesButKeepsOthers()
    {
        var broadcaster = new TelemetryBroadcaster();
        var service = CreateService(broadcaster, PasskeyGateHarness.Create().Gate);

        var written = await StreamAsync(service, broadcaster, grantToken: null, publish: () =>
        {
            broadcaster.PublishLogLine(new LogLineEvent(DateTimeOffset.UtcNow, "INFO", "body text", ContentBearing: true));
            broadcaster.PublishLogLine(new LogLineEvent(DateTimeOffset.UtcNow, "INFO", "plain line"));
        }, expectedCount: 1);

        var line = Assert.Single(written).LogLine;
        Assert.Equal("plain line", line.Message);
        Assert.False(line.ContentBearing);
    }

    [Fact]
    public async Task StreamEvents_WithGrant_DeliversContentBearingLogLines()
    {
        var broadcaster = new TelemetryBroadcaster();
        var harness = PasskeyGateHarness.Create();
        var service = CreateService(broadcaster, harness.Gate);

        var written = await StreamAsync(service, broadcaster, grantToken: harness.Grants.IssueGrant(), publish: () =>
            broadcaster.PublishLogLine(new LogLineEvent(DateTimeOffset.UtcNow, "INFO", "body text", ContentBearing: true)),
            expectedCount: 1);

        var line = Assert.Single(written).LogLine;
        Assert.True(line.ContentBearing);
        Assert.Equal("body text", line.Message);
    }

    [Fact]
    public async Task ListPersistedSessions_WithoutGrant_OmitsTextButKeepsMetadata()
    {
        var service = CreateService(new TelemetryBroadcaster(), PasskeyGateHarness.Create().Gate, [Transcript()]);

        var response = await service.ListPersistedSessions(
            new Contract.ListPersistedSessionsRequest { Limit = 10 },
            PasskeyGateHarness.Context(cancellationToken: TestContext.Current.CancellationToken));

        var row = Assert.Single(response.Transcripts);
        Assert.False(row.HasPromptText);
        Assert.False(row.HasResponseText);
        Assert.Equal("kimi-k2.5", row.RoutedModel);
        Assert.Equal(12, row.PromptTextLength);
    }

    [Fact]
    public async Task ListPersistedSessions_WithGrant_StillOmitsText()
    {
        var harness = PasskeyGateHarness.Create();
        var service = CreateService(new TelemetryBroadcaster(), harness.Gate, [Transcript()]);

        var response = await service.ListPersistedSessions(
            new Contract.ListPersistedSessionsRequest { Limit = 10 },
            PasskeyGateHarness.Context(harness.Grants.IssueGrant(), TestContext.Current.CancellationToken));

        var row = Assert.Single(response.Transcripts);
        Assert.False(row.HasPromptText);
        Assert.False(row.HasResponseText);
        Assert.Equal(12, row.PromptTextLength);
    }

    [Fact]
    public async Task GetTurnTexts_WithGrant_ReturnsExtracts()
    {
        var harness = PasskeyGateHarness.Create();
        var store = new Mock<ITranscriptStore>();
        store.Setup(s => s.LoadTurnTextsAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new StoredTurnText(1, true, "fix this bug", "here is the fix", 12, 15)
            ]);
        var service = new TelemetryGrpcService(
            broadcaster: new TelemetryBroadcaster(),
            transcriptStore: store.Object,
            transcriptOptions: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }),
            contentGate: harness.Gate);

        var response = await service.GetTurnTexts(
            new Contract.GetTurnTextsRequest { TranscriptIds = { 1 } },
            PasskeyGateHarness.Context(harness.Grants.IssueGrant(), TestContext.Current.CancellationToken));

        var text = Assert.Single(response.Texts);
        Assert.Equal("fix this bug", text.PromptText);
        Assert.Equal("here is the fix", text.ResponseText);
    }

    [Fact]
    public async Task GetTurnTexts_WithoutGrant_IsRejected()
    {
        var service = CreateService(new TelemetryBroadcaster(), PasskeyGateHarness.Create().Gate, [Transcript()]);

        var error = await Assert.ThrowsAsync<RpcException>(() => service.GetTurnTexts(
            new Contract.GetTurnTextsRequest { TranscriptIds = { 1 } },
            PasskeyGateHarness.Context(cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    private sealed class CollectingWriter : IServerStreamWriter<Contract.TelemetryEvent>
    {
        public List<Contract.TelemetryEvent> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Contract.TelemetryEvent message)
        {
            lock (Written) Written.Add(message);
            return Task.CompletedTask;
        }
    }
}
