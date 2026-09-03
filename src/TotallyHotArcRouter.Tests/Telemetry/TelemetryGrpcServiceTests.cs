using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryGrpcService.StreamEvents"/> (registration with
/// <see cref="TelemetryBroadcaster"/>, forwarding published events to the response stream, and
/// unregistering when the call is cancelled) and <see cref="TelemetryGrpcService.ListPersistedSessions"/>
/// (docs/router/sessions-tab-training-data-plan.md Phase 1). Unit-tested directly against a
/// <see cref="TestServerCallContext"/> and an in-memory <see cref="IServerStreamWriter{T}"/> fake
/// (see docs/router/grpc-migration.md's "Testing changes" - a full <c>TestHost</c>/<c>Grpc.Net.Client</c>
/// integration harness is heavier than this method's logic needs).
/// </summary>
public class TelemetryGrpcServiceTests
{
    private static TelemetryGrpcService CreateService(
        TelemetryBroadcaster? broadcaster = null,
        IReadOnlyList<SessionTranscript>? sessions = null,
        bool transcriptCaptureEnabled = true)
    {
        return new TelemetryGrpcService(
            broadcaster: broadcaster ?? new TelemetryBroadcaster(),
            transcriptStore: new FakeTranscriptStore(sessions ?? []),
            transcriptOptions: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions
                { Enabled = transcriptCaptureEnabled }));
    }

    private static ServerCallContext CreateContext(CancellationToken cancellationToken)
    {
        return TestServerCallContext.Create(
            method: "StreamEvents",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for condition.");

            await Task.Delay(10, cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public void Constructor_NullBroadcaster_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryGrpcService(
            broadcaster: null!,
            transcriptStore: new FakeTranscriptStore([]),
            transcriptOptions: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions())));
    }

    [Fact]
    public void Constructor_NullTranscriptStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryGrpcService(
            broadcaster: new TelemetryBroadcaster(),
            transcriptStore: null!,
            transcriptOptions: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions())));
    }

    [Fact]
    public void Constructor_NullTranscriptOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryGrpcService(
            broadcaster: new TelemetryBroadcaster(),
            transcriptStore: new FakeTranscriptStore([]),
            transcriptOptions: null!));
    }

    [Fact]
    public async Task StreamEvents_DeliversPublishedEventsToResponseStream()
    {
        var broadcaster = new TelemetryBroadcaster();
        var service = CreateService(broadcaster);
        var writer = new FakeServerStreamWriter<Contract.TelemetryEvent>();
        using var cts = new CancellationTokenSource();

        // StreamEvents registers with the broadcaster synchronously, before its first await (the
        // await foreach's initial MoveNextAsync on the not-yet-populated channel) - so by the time
        // this call returns a Task, registration has already happened and Publish below is safe.
        var callTask = service.StreamEvents(request: new Contract.StreamEventsRequest(), responseStream: writer,
            context: CreateContext(cts.Token));

        var telemetryEvent = SampleEvent();
        broadcaster.Publish(telemetryEvent);

        await WaitUntilAsync(condition: () => writer.Written.Count > 0, timeout: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(writer.Written);
        Assert.Equal(expected: Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry,
            actual: writer.Written[0].EventCase);
        Assert.Equal(expected: telemetryEvent.SessionId, actual: writer.Written[0].RoutingTelemetry.SessionId);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callTask);
    }

    [Fact]
    public async Task StreamEvents_UnregistersFromBroadcasterWhenCallEnds()
    {
        var broadcaster = new TelemetryBroadcaster();
        var service = CreateService(broadcaster);
        var writer = new FakeServerStreamWriter<Contract.TelemetryEvent>();
        using var cts = new CancellationTokenSource();

        var callTask = service.StreamEvents(request: new Contract.StreamEventsRequest(), responseStream: writer,
            context: CreateContext(cts.Token));

        broadcaster.Publish(SampleEvent());
        await WaitUntilAsync(condition: () => writer.Written.Count > 0, timeout: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callTask);

        // The call ended (finally { _broadcaster.Unregister(...) } ran), so a further publish must
        // not reach this call's now-abandoned writer.
        broadcaster.Publish(SampleEvent());
        await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(writer.Written);
    }

    [Fact]
    public async Task ListPersistedSessions_CaptureDisabled_ReturnsFalseFlagAndEmptyListWithoutQueryingTheStore()
    {
        var service = CreateService(
            sessions: [SampleSessionTranscript()],
            transcriptCaptureEnabled: false);

        var response = await service.ListPersistedSessions(
            request: new Contract.ListPersistedSessionsRequest { Limit = 10 },
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.False(response.TranscriptCaptureEnabled);
        Assert.Empty(response.Transcripts);
    }

    [Fact]
    public async Task ListPersistedSessions_CaptureEnabled_MapsEveryFieldOntoTheContract()
    {
        var transcript = SampleSessionTranscript();
        var service = CreateService(sessions: [transcript], transcriptCaptureEnabled: true);

        var response = await service.ListPersistedSessions(
            request: new Contract.ListPersistedSessionsRequest { Limit = 10 },
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.True(response.TranscriptCaptureEnabled);
        var mapped = Assert.Single(response.Transcripts);
        Assert.Equal(expected: transcript.SessionId, actual: mapped.SessionId);
        Assert.Equal(expected: transcript.CorrelationId, actual: mapped.CorrelationId);
        Assert.Equal(expected: transcript.RequestedModel, actual: mapped.RequestedModel);
        Assert.Equal(expected: transcript.RoutedModel, actual: mapped.RoutedModel);
        Assert.Equal(expected: transcript.PromptText, actual: mapped.PromptText);
        Assert.Equal(expected: transcript.ResponseText, actual: mapped.ResponseText);
        Assert.Equal(expected: "0.0042", actual: mapped.CostUsd);
        Assert.Equal(expected: transcript.InputTokens, actual: mapped.InputTokens);
        Assert.Equal(expected: transcript.OutputTokens, actual: mapped.OutputTokens);
        Assert.Equal(expected: transcript.MemoryEntryId, actual: mapped.MemoryEntryId);
    }

    [Fact]
    public async Task ListPersistedSessions_RowWithNoOptionalFields_LeavesThemUnset()
    {
        var transcript = new SessionTranscript(
            2,
            SessionId: "sess-bare",
            CorrelationId: "sess-bare:1",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "gpt-5.4",
            RoutedModel: "gpt-5.4",
            null,
            null,
            null,
            null,
            null,
            null);
        var service = CreateService(sessions: [transcript], transcriptCaptureEnabled: true);

        var response = await service.ListPersistedSessions(
            request: new Contract.ListPersistedSessionsRequest { Limit = 10 },
            context: CreateContext(TestContext.Current.CancellationToken));

        var mapped = Assert.Single(response.Transcripts);
        Assert.False(mapped.HasPromptText);
        Assert.False(mapped.HasResponseText);
        Assert.False(mapped.HasCostUsd);
        Assert.False(mapped.HasInputTokens);
        Assert.False(mapped.HasOutputTokens);
        Assert.False(mapped.HasMemoryEntryId);
    }

    private static SessionTranscript SampleSessionTranscript()
    {
        return new SessionTranscript(
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
            7);
    }

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal <see cref="ITranscriptStore"/> fake returning a fixed, pre-seeded session list.</summary>
    private sealed class FakeTranscriptStore(IReadOnlyList<SessionTranscript> sessions) : ITranscriptStore
    {
        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SessionTranscript>> ListSessionsAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(sessions);
        }
    }
}