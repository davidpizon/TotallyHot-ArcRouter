using Grpc.Core;
using Grpc.Core.Testing;
using Grpc.Net.Client;
using System.Globalization;
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
    /// <summary>
    /// <c>Grpc.Net.Client</c>'s default <see cref="GrpcChannelOptions.MaxReceiveMessageSize"/>, read from the
    /// library rather than restated. Every GUI channel runs at it: nothing in <c>src/</c> overrides it
    /// (<c>WasmRouterChannelProvider</c>, <c>TelemetryChannelFactory</c>).
    /// </summary>
    private static readonly int DefaultClientMaxReceiveMessageSize =
        new GrpcChannelOptions().MaxReceiveMessageSize!.Value;

    /// <summary>The row count <c>PersistedSessionStore.RequestLimit</c> asks for on every Sessions-tab load.</summary>
    private const int GuiRequestLimit = 500;

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
        await callTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
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
        await callTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

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

    /// <summary>
    /// Measures the serialized response at the GUI's 500-row request against the client's default receive
    /// cap, for representative per-row text sizes. Sizes are UTF-8 bytes of ASCII text, so bytes equal
    /// characters. This characterizes today's wire shape, which carries every row's full text: a response
    /// over the cap fails on the client with <see cref="StatusCode.ResourceExhausted"/> (pinned in
    /// <c>PersistedSessionsClientTests</c>), and the Sessions tab then shows no persisted history.
    /// </summary>
    [Theory]
    // This machine's transcripts.db, sampled 2026-09-30: 82 rows, mean 362-byte prompt and 76-byte response.
    [InlineData(362, 76, false)]
    // Chat style: a short question and a medium answer.
    [InlineData(500, 2_000, false)]
    // Agentic, with substantial final answers.
    [InlineData(2_000, 4_000, false)]
    // A Copilot-style agent loop. The newest user message carries attached files and editor context, and it
    // is captured again on every tool-call iteration, because tool results arrive as role "tool" messages
    // that RequestTextExtractor skips.
    [InlineData(12_000, 1_000, true)]
    public async Task ListPersistedSessions_AtTheGuiRowLimit_ExceedsTheDefaultClientReceiveCapOnlyWithHeavyText(
        int promptBytes, int responseBytes, bool expectedToExceedCap)
    {
        var size = await SerializedListSizeAsync(rowCount: GuiRequestLimit, promptText: new string('p', promptBytes),
            responseText: new string('r', responseBytes));

        TestContext.Current.TestOutputHelper?.WriteLine(
            FormattableString.Invariant(
                $"{GuiRequestLimit} rows x ({promptBytes} + {responseBytes}) text bytes = {size:N0} serialized bytes ({100.0 * size / DefaultClientMaxReceiveMessageSize:F1}% of the {DefaultClientMaxReceiveMessageSize:N0}-byte cap)"));
        Assert.Equal(expected: expectedToExceedCap, actual: size > DefaultClientMaxReceiveMessageSize);
    }

    /// <summary>
    /// Pins where the GUI's 500-row load starts failing: just above 8 KB of combined prompt and response text
    /// per row. Solved from one measurement, then checked on both sides of the boundary against the service
    /// itself. Between 128 and 16,383 bytes every text length and row length is a two-byte varint, so each
    /// extra text byte per row adds exactly <see cref="GuiRequestLimit"/> serialized bytes. The upper bound
    /// is the cap divided by the row count, the break-even with zero per-row overhead.
    /// </summary>
    [Fact]
    public async Task ListPersistedSessions_AtTheGuiRowLimit_CrossesTheDefaultClientReceiveCapJustAboveEightKilobytesOfTextPerRow()
    {
        const int probeTextBytes = 4_000;
        var probeSize = await SerializedListSizeWithTextPerRowAsync(probeTextBytes);
        var breakEvenTextBytes =
            probeTextBytes + (DefaultClientMaxReceiveMessageSize - probeSize) / GuiRequestLimit;

        var atBreakEven = await SerializedListSizeWithTextPerRowAsync(breakEvenTextBytes);
        var oneByteMore = await SerializedListSizeWithTextPerRowAsync(breakEvenTextBytes + 1);

        TestContext.Current.TestOutputHelper?.WriteLine(
            FormattableString.Invariant(
                $"Break-even: {breakEvenTextBytes:N0} text bytes per row ({atBreakEven:N0} serialized bytes); {breakEvenTextBytes + 1:N0} gives {oneByteMore:N0}. Per-row overhead: {DefaultClientMaxReceiveMessageSize / GuiRequestLimit - breakEvenTextBytes} bytes."));
        Assert.InRange(actual: atBreakEven, low: 0, high: DefaultClientMaxReceiveMessageSize);
        Assert.InRange(actual: oneByteMore, low: DefaultClientMaxReceiveMessageSize + 1, high: int.MaxValue);
        Assert.InRange(actual: breakEvenTextBytes, low: 8_000, high: DefaultClientMaxReceiveMessageSize / GuiRequestLimit);
    }

    /// <summary>
    /// Shows that lowering the row limit cannot bound the message by itself. <c>prompt_text</c> is the newest
    /// user message verbatim. Only Kestrel's default 30,000,000-byte request body limit bounds it, and nothing
    /// in <c>src/</c> lowers that limit. A row is persisted even when the provider rejects the prompt and the
    /// error is relayed to the client. So one pasted 4 MiB log fails the load at <c>limit = 1</c>.
    /// </summary>
    [Fact]
    public async Task ListPersistedSessions_OneRowWithAFourMebibytePrompt_AloneExceedsTheDefaultClientReceiveCap()
    {
        var size = await SerializedListSizeAsync(rowCount: 1, promptText: new string('p', 4 * 1024 * 1024),
            responseText: null);

        Assert.InRange(actual: size, low: DefaultClientMaxReceiveMessageSize + 1, high: int.MaxValue);
    }

    /// <summary>
    /// Serializes what <see cref="TelemetryGrpcService.ListPersistedSessions"/> returns for
    /// <paramref name="rowCount"/> rows that carry the given texts. The result is the payload length
    /// <c>Grpc.Net.Client</c> compares against its receive cap; the 5-byte gRPC frame header is not counted.
    /// </summary>
    private static async Task<int> SerializedListSizeAsync(int rowCount, string? promptText, string? responseText)
    {
        var service = CreateService(sessions: RealisticRows(count: rowCount, promptText: promptText,
            responseText: responseText));

        var response = await service.ListPersistedSessions(
            request: new Contract.ListPersistedSessionsRequest { Limit = rowCount },
            context: CreateContext(TestContext.Current.CancellationToken));

        Assert.Equal(expected: rowCount, actual: response.Transcripts.Count);
        return response.CalculateSize();
    }

    /// <summary>
    /// <see cref="SerializedListSizeAsync"/> at the GUI's row limit, with <paramref name="textBytes"/> of ASCII
    /// text per row split evenly between prompt and response.
    /// </summary>
    private static Task<int> SerializedListSizeWithTextPerRowAsync(int textBytes)
    {
        return SerializedListSizeAsync(rowCount: GuiRequestLimit, promptText: new string('p', textBytes / 2),
            responseText: new string('r', textBytes - textBytes / 2));
    }

    /// <summary>
    /// <paramref name="count"/> newest-first rows whose metadata is shaped like the sampled production
    /// <c>transcripts.db</c>: 32-character session ids with 25 turns each, sub-second timestamps, a cost read
    /// back from a REAL column, and an agentic client's token counts. Only the text varies between the size
    /// tests above, and every row shares one instance of each string.
    /// </summary>
    private static List<SessionTranscript> RealisticRows(int count, string? promptText, string? responseText)
    {
        var newest = new DateTimeOffset(2026, 9, 30, 12, 0, 0, offset: TimeSpan.Zero).AddTicks(1_234_567);

        return
        [
            .. Enumerable.Range(0, count).Select(i =>
            {
                var sessionId = (i / 25).ToString(format: "x32", provider: CultureInfo.InvariantCulture);
                return new SessionTranscript(
                    Id: count - i,
                    SessionId: sessionId,
                    CorrelationId: FormattableString.Invariant($"{sessionId}:{25 - i % 25}"),
                    CreatedAtUtc: newest.AddSeconds(-7 * i),
                    RequestedModel: "claude-sonnet-4-5",
                    RoutedModel: "kimi-k2.5",
                    PromptText: promptText,
                    ResponseText: responseText,
                    Cost: (decimal)0.0123456789,
                    InputTokens: 41_246,
                    OutputTokens: 255,
                    MemoryEntryId: null);
            })
        ];
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

        public Task UpdateOutcomeAsync(string correlationId, double? score, bool isJudgeScored = false,
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