using Grpc.Core;
using Grpc.Core.Testing;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
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
        bool transcriptCaptureEnabled = true,
        FakeTranscriptStore? store = null,
        ILogger<TelemetryGrpcService>? logger = null)
    {
        return new TelemetryGrpcService(
            broadcaster: broadcaster ?? new TelemetryBroadcaster(),
            transcriptStore: store ?? new FakeTranscriptStore(sessions ?? []),
            transcriptOptions: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions
            { Enabled = transcriptCaptureEnabled }),
            logger: logger);
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
        Assert.Equal(expected: transcript.Id, actual: mapped.TranscriptId);
        Assert.Equal(expected: transcript.PromptTextLength, actual: mapped.PromptTextLength);
        Assert.Equal(expected: transcript.ResponseTextLength, actual: mapped.ResponseTextLength);
        Assert.False(mapped.PromptTruncated);
        Assert.False(mapped.ResponseTruncated);
        Assert.False(response.HasMore);
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
        Assert.False(mapped.HasPromptTextLength);
        Assert.False(mapped.HasResponseTextLength);
        Assert.False(mapped.PromptTruncated);
        Assert.False(mapped.ResponseTruncated);
    }

    /// <summary>
    /// The response stays within <see cref="TelemetryGrpcService.MaxListResponseBytes"/>, and so under the GUI
    /// client's default receive cap, for every text profile the #179 investigation measured. Before the fix the
    /// last three profiles failed to load. Sizes are UTF-8 bytes of ASCII text, so bytes equal characters.
    /// </summary>
    [Theory]
    // This machine's transcripts.db, sampled 2026-09-30: 82 rows, mean 362-byte prompt and 76-byte response.
    [InlineData(362, 76)]
    // Chat style: a short question and a medium answer.
    [InlineData(500, 2_000)]
    // Agentic, with substantial final answers.
    [InlineData(2_000, 4_000)]
    // Just past the old 8,244-byte break-even.
    [InlineData(4_123, 4_122)]
    // A Copilot-style agent loop. The newest user message carries attached files and editor context, and it
    // is captured again on every tool-call iteration, because tool results arrive as role "tool" messages
    // that RequestTextExtractor skips.
    [InlineData(12_000, 1_000)]
    // 13 KB per row, the plan's Phase 1 exit criterion.
    [InlineData(6_500, 6_500)]
    public async Task ListPersistedSessions_AtTheGuiRowLimit_StaysWithinTheBudgetAndReturnsEveryAsciiRow(
        int promptBytes, int responseBytes)
    {
        var response = await ListAsync(rows: RealisticRows(count: GuiRequestLimit,
                promptText: new string('p', promptBytes), responseText: new string('r', responseBytes)),
            limit: GuiRequestLimit);
        var size = response.CalculateSize();

        TestContext.Current.TestOutputHelper?.WriteLine(
            FormattableString.Invariant(
                $"{GuiRequestLimit} rows x ({promptBytes} + {responseBytes}) text bytes = {size:N0} serialized bytes ({100.0 * size / DefaultClientMaxReceiveMessageSize:F1}% of the {DefaultClientMaxReceiveMessageSize:N0}-byte cap)"));
        Assert.InRange(actual: size, low: 0, high: TelemetryGrpcService.MaxListResponseBytes);
        // ASCII previews are at most about 4.2 KB a row, so 500 of them fit with room to spare.
        Assert.Equal(expected: GuiRequestLimit, actual: response.Transcripts.Count);
        Assert.False(response.HasMore);
    }

    /// <summary>
    /// The character cap alone is not a byte guarantee: 2,000 CJK characters are 6,000 UTF-8 bytes, so 500 rows
    /// of CJK previews come to about 6 MB. The byte budget cuts the list, reports <c>has_more</c>, and keeps the
    /// newest rows, each one whole.
    /// </summary>
    [Fact]
    public async Task ListPersistedSessions_CjkTextAtTheGuiRowLimit_IsCutByTheBudgetToTheNewestWholeRows()
    {
        var cjk = new string('漢', 5_000);
        var rows = RealisticRows(count: GuiRequestLimit, promptText: cjk, responseText: cjk);
        var logger = new CapturingLogger();

        var response = await ListAsync(rows: rows, limit: GuiRequestLimit, logger: logger);

        Assert.InRange(actual: response.CalculateSize(), low: 0, high: TelemetryGrpcService.MaxListResponseBytes);
        Assert.True(response.HasMore);
        Assert.InRange(actual: response.Transcripts.Count, low: 200, high: GuiRequestLimit - 1);
        var expectedPreview = TextTruncator.Truncate(cjk);
        for (var i = 0; i < response.Transcripts.Count; i++)
        {
            Assert.Equal(expected: rows[i].Id, actual: response.Transcripts[i].TranscriptId);
            Assert.Equal(expected: expectedPreview, actual: response.Transcripts[i].PromptText);
            Assert.Equal(expected: expectedPreview, actual: response.Transcripts[i].ResponseText);
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expected: LogLevel.Information, actual: entry.Level);
        Assert.Contains(expectedSubstring: FormattableString.Invariant($"returned {response.Transcripts.Count} of {GuiRequestLimit} rows"),
            actualString: entry.Message);
    }

    /// <summary>
    /// Proves the budget accounting is exact, not merely conservative. One row is sized, through its
    /// client-supplied <c>requested_model</c>, which is not truncated, so the counted response lands exactly on
    /// <see cref="TelemetryGrpcService.MaxListResponseBytes"/>: it is returned. One byte more and it is not,
    /// with <c>has_more</c> set. Every length prefix is 4 bytes throughout, so a row's size grows one byte per
    /// filler byte.
    /// </summary>
    [Fact]
    public async Task ListPersistedSessions_RowLandingExactlyOnTheBudget_IsReturnedAndOneByteMoreIsNot()
    {
        const int probeLength = 3_000_000;
        var probe = await ListAsync(rows: [RowWithRequestedModelLength(probeLength)], limit: 1);
        // The response as counted: what was serialized, plus the 2 bytes reserved for has_more.
        var countedAtProbe = probe.CalculateSize() + 2;
        var exactLength = probeLength + (TelemetryGrpcService.MaxListResponseBytes - countedAtProbe);

        var atBudget = await ListAsync(rows: [RowWithRequestedModelLength(exactLength)], limit: 1);
        var overBudget = await ListAsync(rows: [RowWithRequestedModelLength(exactLength + 1)], limit: 1);

        Assert.Single(atBudget.Transcripts);
        Assert.False(atBudget.HasMore);
        Assert.Equal(expected: TelemetryGrpcService.MaxListResponseBytes - 2, actual: atBudget.CalculateSize());
        Assert.Empty(overBudget.Transcripts);
        Assert.True(overBudget.HasMore);
    }

    /// <summary>
    /// One pasted 4 MiB prompt failed the load at any row limit before the fix: <c>prompt_text</c> is bounded
    /// only by Kestrel's default 30,000,000-byte request body limit, and a row is persisted even when the
    /// provider rejects the prompt. Now the row loads as a preview that says it was cut, with its stored length.
    /// </summary>
    [Fact]
    public async Task ListPersistedSessions_OneRowWithAFourMebibytePrompt_LoadsAsAFlaggedPreview()
    {
        var prompt = new string('p', 4 * 1024 * 1024);
        var row = RealisticRows(count: 1, promptText: prompt, responseText: "ok")[0] with
        {
            PromptTextLength = prompt.Length,
            ResponseTextLength = 2
        };

        var response = await ListAsync(rows: [row], limit: 1);

        Assert.InRange(actual: response.CalculateSize(), low: 0, high: TelemetryGrpcService.MaxListResponseBytes);
        var mapped = Assert.Single(response.Transcripts);
        Assert.Equal(expected: TextTruncator.Truncate(prompt), actual: mapped.PromptText);
        Assert.True(mapped.PromptTruncated);
        Assert.Equal(expected: prompt.Length, actual: mapped.PromptTextLength);
        Assert.Equal(expected: "ok", actual: mapped.ResponseText);
        Assert.False(mapped.ResponseTruncated);
        Assert.Equal(expected: 2, actual: mapped.ResponseTextLength);
    }

    /// <summary>
    /// Previews equal <see cref="TextTruncator.Truncate"/> of the stored text, the live telemetry preview, and
    /// the truncation flags are set exactly when a preview was cut: at the cap a text is sent whole, one
    /// character over it is cut.
    /// </summary>
    [Theory]
    [InlineData(TextTruncator.DefaultMaxLength, false)]
    [InlineData(TextTruncator.DefaultMaxLength + 1, true)]
    public async Task ListPersistedSessions_Previews_MatchLiveTelemetryAndFlagExactlyWhenCut(int length,
        bool expectedTruncated)
    {
        var text = new string('x', length);
        var response = await ListAsync(rows: RealisticRows(count: 1, promptText: text, responseText: text),
            limit: 1);

        var mapped = Assert.Single(response.Transcripts);
        Assert.Equal(expected: TextTruncator.Truncate(text), actual: mapped.PromptText);
        Assert.Equal(expected: TextTruncator.Truncate(text), actual: mapped.ResponseText);
        Assert.Equal(expected: expectedTruncated, actual: mapped.PromptTruncated);
        Assert.Equal(expected: expectedTruncated, actual: mapped.ResponseTruncated);
    }

    /// <summary>
    /// The limit is clamped: unset (0) means 500, anything else is clamped to [1, 2,000]. The store is asked for
    /// one row more than the clamped limit, which is how <c>has_more</c> is detected. An unset limit used to
    /// reach the store as 0 and fail the call with <see cref="StatusCode.Unknown"/>.
    /// </summary>
    [Theory]
    [InlineData(0, 500)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(2_000, 2_000)]
    [InlineData(5_000, 2_000)]
    public async Task ListPersistedSessions_ClampsTheLimitAndAsksTheStoreForOneMore(int requested, int clamped)
    {
        var store = new FakeTranscriptStore([]);

        await ListAsync(store: store, limit: requested);

        Assert.Equal(expected: clamped + 1, actual: store.RequestedLimit);
    }

    /// <summary><c>has_more</c> is set when the store holds more rows than the limit, and only then.</summary>
    [Theory]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    public async Task ListPersistedSessions_MoreRowsThanTheLimit_SetsHasMore(int stored, int limit, bool expected)
    {
        var rows = RealisticRows(count: stored, promptText: "p", responseText: "r");

        var response = await ListAsync(rows: rows, limit: limit);

        Assert.Equal(expected: Math.Min(val1: stored, val2: limit), actual: response.Transcripts.Count);
        Assert.Equal(expected: expected, actual: response.HasMore);
    }

    /// <summary>Calls <see cref="TelemetryGrpcService.ListPersistedSessions"/> over a fake store.</summary>
    private static Task<Contract.ListPersistedSessionsResponse> ListAsync(int limit,
        IReadOnlyList<SessionTranscript>? rows = null, FakeTranscriptStore? store = null,
        ILogger<TelemetryGrpcService>? logger = null)
    {
        var service = CreateService(sessions: rows, store: store, logger: logger);

        return service.ListPersistedSessions(
            request: new Contract.ListPersistedSessionsRequest { Limit = limit },
            context: CreateContext(TestContext.Current.CancellationToken));
    }

    /// <summary>One row whose only large field is a <c>requested_model</c> of <paramref name="length"/> characters.</summary>
    private static SessionTranscript RowWithRequestedModelLength(int length)
    {
        return RealisticRows(count: 1, promptText: "p", responseText: "r")[0] with
        {
            RequestedModel = new string('m', length)
        };
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
            7,
            PromptTextLength: 12,
            ResponseTextLength: 15);
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

    /// <summary>Records each entry's level and rendered message.</summary>
    private sealed class CapturingLogger : ILogger<TelemetryGrpcService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(arg1: state, arg2: exception)));
        }
    }

    /// <summary>
    /// Minimal <see cref="ITranscriptStore"/> fake over a fixed, pre-seeded, newest-first session list. Honours
    /// the requested limit, as <see cref="SqliteTranscriptStore"/> does, and records it.
    /// </summary>
    private sealed class FakeTranscriptStore(IReadOnlyList<SessionTranscript> sessions) : ITranscriptStore
    {
        /// <summary>The limit of the most recent <see cref="ListSessionsAsync"/> call.</summary>
        public int? RequestedLimit { get; private set; }

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
            RequestedLimit = limit;
            return Task.FromResult<IReadOnlyList<SessionTranscript>>([.. sessions.Take(limit)]);
        }
    }
}