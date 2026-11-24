using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins ADR-0018's writer: enqueue returns before SQLite, a full channel waits, shutdown logs what it
/// abandons, and a stale epoch still records spend.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class ServedRequestWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(TestScratchDirectory.RunRoot, "served-writer-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly string _databasePath;
    private readonly TranscriptDatabase _database;
    private readonly SessionIndex _index;
    private readonly SessionStore _store;

    /// <summary>Builds a store over a scratch database and folder with an in-memory master key.</summary>
    public ServedRequestWriterTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Path.Combine(_root, "sessions");
        _databasePath = Path.Combine(_root, "transcripts.db");
        _database = new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = _databasePath }));
        _index = new SessionIndex(_database);
        _index.EnsureCreated();
        _store = new SessionStore(_index, new InMemoryMasterKeyStore(), _folder);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            try
            {
                using var connection = _database.OpenConnection();
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Best-effort cleanup. A database already going away is not a test failure.
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
    }

    /// <summary>Enqueue returns before the ledger is entered. The row exists once the writer is released.</summary>
    [Fact]
    public async Task Enqueue_ReturnsBeforeTheLedgerWrite()
    {
        var ledger = new GateLedger();
        var log = new ListLogger<ServedRequestWriter>();
        using var writer = NewWriter(ledger: ledger, logger: log);
        var item = new ServedRequest(Work("s", 1), null, null, 0);

        Assert.True(await writer.EnqueueAsync(item));
        Assert.False(ledger.Entered.Task.IsCompleted);

        await writer.StartAsync(CancellationToken.None);
        await ledger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ledger.Release.TrySetResult();
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, ledger.Calls);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>A full channel logs a warning and waits. It does not drop the second spend row.</summary>
    [Fact]
    public async Task FullChannel_WaitsAndDoesNotDrop()
    {
        var ledger = new GateLedger();
        var log = new ListLogger<ServedRequestWriter>();
        using var writer = NewWriter(ledger: ledger, logger: log, capacity: 1);
        await writer.StartAsync(CancellationToken.None);

        Assert.True(await writer.EnqueueAsync(new ServedRequest(Work("s", 1, "first"), null, null, 0)));
        await ledger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = writer.EnqueueAsync(new ServedRequest(Work("s", 2, "second"), null, null, 0)).AsTask();
        await log.WaitForWarningAsync("The served-request queue is full; waiting for the writer.");
        Assert.False(second.IsCompleted);

        ledger.Release.TrySetResult();
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, ledger.Calls);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>Index inserts follow enqueue order.</summary>
    [Fact]
    public async Task IndexInserts_FollowEnqueueOrder()
    {
        var inserts = new List<string>();
        using var writer = NewWriter(transcript: RecordingTranscript(inserts));
        await writer.StartAsync(CancellationToken.None);

        await writer.EnqueueAsync(new ServedRequest(Work("a", 1), null, null, 0));
        await writer.EnqueueAsync(new ServedRequest(Work("b", 2), null, null, 0));
        await writer.EnqueueAsync(new ServedRequest(Work("c", 3), null, null, 0));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(["a:1", "b:2", "c:3"], inserts);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>Shutdown waits out a short drain, logs the abandoned count, and does not throw.</summary>
    [Fact]
    public async Task Shutdown_DrainThenAbandon_LogsTheCount()
    {
        var ledger = new GateLedger();
        var log = new ListLogger<ServedRequestWriter>();
        using var writer = NewWriter(ledger: ledger, logger: log, capacity: 1, drain: TimeSpan.FromMilliseconds(30));
        await writer.StartAsync(CancellationToken.None);
        await writer.EnqueueAsync(new ServedRequest(Work("s", 1), null, null, 0));
        await ledger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await writer.StopAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(200)).Token);

        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("Served-request shutdown timed out with", StringComparison.Ordinal)
            && entry.Message.Contains('1'));
        ledger.Release.TrySetResult();
    }

    /// <summary>The writer's gauge hold outlives the request-scope hold and returns to zero when the row finishes.</summary>
    [Fact]
    public async Task Gauge_StaysNonZeroUntilTheWriterFinishes()
    {
        var ledger = new GateLedger();
        var gauge = new InFlightRequestGauge();
        using var writer = NewWriter(ledger: ledger);
        await writer.StartAsync(CancellationToken.None);

        using (gauge.Track())
        {
            Assert.Equal(1, gauge.Count);
        }

        Assert.Equal(0, gauge.Count);
        Assert.True(await writer.EnqueueAsync(new ServedRequest(Work("s", 1), null, null, 0), gauge));
        await ledger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gauge.Count > 0);

        ledger.Release.TrySetResult();
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, gauge.Count);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>A current-epoch turn is appended to the session file before the transcript insert.</summary>
    [Fact]
    public async Task CommitOrder_AppendsTheSessionFileBeforeTheIndex()
    {
        var order = new List<string>();
        var transcript = new Mock<ITranscriptStore>();
        transcript.Setup(store => store.InsertAsync(It.IsAny<TranscriptRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranscriptRecord _, CancellationToken _) =>
            {
                order.Add(_store.ListTurns(_store.ResolveArchiveSessionId("client")).Count > 0 ? "index-after-file" : "index-before-file");
                return 1L;
            });
        using var writer = NewWriter(transcript: transcript.Object);
        await writer.StartAsync(CancellationToken.None);

        var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryWrite("response"u8));
        Assert.True(spool.TryComplete());
        var capture = new SessionCaptureItem(
            "client",
            new SessionTurnInput(Guid.CreateVersion7(), DateTimeOffset.UtcNow,
            [
                new SessionBodyInput(SessionBodyKind.ClientRequest, "request"u8.ToArray()),
                new SessionBodyInput(SessionBodyKind.ClientResponse, null, spool),
            ]));
        await writer.EnqueueAsync(new ServedRequest(
            Work("client", 1), capture, null, capture.Epoch));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(["index-after-file"], order);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>An index failure after the flush is logged, does not append again, and drops only a bound archive.</summary>
    [Fact]
    public async Task IndexFailure_AfterFlush_LogsAndDropsOnlyWhenBound()
    {
        var log = new ListLogger();
        var drops = 0;
        var transcript = new Mock<ITranscriptStore>();
        transcript.Setup(store => store.InsertAsync(It.IsAny<TranscriptRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("index down"));
        using var writer = NewWriter(transcript: transcript.Object, publisherLogger: log);
        await writer.StartAsync(CancellationToken.None);

        var bound = CaptureItem("bound");
        await writer.EnqueueAsync(new ServedRequest(
            Work("bound", 1, resolve: _ => new ArchiveBinding(Guid.CreateVersion7(), Guid.CreateVersion7())),
            bound, () => drops++, bound.Epoch));
        var unbound = CaptureItem("unbound");
        await writer.EnqueueAsync(new ServedRequest(Work("unbound", 1), unbound, () => drops++, unbound.Epoch));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        Assert.Single(_store.ListTurns(_store.ResolveArchiveSessionId("bound")));
        Assert.Single(_store.ListTurns(_store.ResolveArchiveSessionId("unbound")));
        Assert.Equal(1, drops);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("Failed to write a transcript row", StringComparison.Ordinal));
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>A ledger throw still leaves the session file and the index, and skips the live event.</summary>
    [Fact]
    public async Task SpendFailure_DoesNotSkipTheFileOrTheIndex()
    {
        var ledger = new GateLedger { Throw = true };
        var events = new List<CancellationToken>();
        var inserts = new List<string>();
        using var writer = NewWriter(ledger: ledger, transcript: RecordingTranscript(inserts), telemetry: events);
        await writer.StartAsync(CancellationToken.None);

        var capture = CaptureItem("spent");
        await writer.EnqueueAsync(new ServedRequest(Work("spent", 1), capture, null, capture.Epoch));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        Assert.Single(_store.ListTurns(_store.ResolveArchiveSessionId("spent")));
        Assert.Equal(["spent:1"], inserts);
        Assert.Empty(events);
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>A stale epoch writes no session file, still records spend and the transcript, and drops only a bound turn.</summary>
    [Fact]
    public async Task StaleEpoch_SkipsTheFileAndDropsOnlyWhenBound()
    {
        var epoch = new CaptureEpoch();
        var started = epoch.Current;
        epoch.Advance();
        var ledger = new GateLedger();
        var inserts = new List<string>();
        var drops = new List<Guid>();
        using var writer = NewWriter(ledger: ledger, transcript: RecordingTranscript(inserts), epoch: epoch);
        await writer.StartAsync(CancellationToken.None);

        var pump = new CaptureBodyPump(Options.Create(new SessionCaptureOptions()), NullLogger<CaptureBodyPump>.Instance);
        var bound = new TurnCapture(
            pump, null, new CaptureEpoch(), () => SessionBodySpool.Create(_folder), NullLogger.Instance,
            DateTimeOffset.UtcNow, resolveArchiveSession: _ => Guid.CreateVersion7(), onTurnDropped: drops.Add);
        bound.BindArchive("stale-bound");
        var boundItem = await bound.FinishAsync(Turn("stale-bound", 1), new DefaultHttpContext(), false);
        Assert.NotNull(boundItem);
        var staleBound = new SessionCaptureItem(boundItem.ClientSessionId, boundItem.Turn, started);
        await writer.EnqueueAsync(new ServedRequest(
            Work("stale-bound", 1), staleBound, bound.NotifyDroppedOnce, started));

        var unbound = new TurnCapture(
            pump, null, new CaptureEpoch(), () => SessionBodySpool.Create(_folder), NullLogger.Instance,
            DateTimeOffset.UtcNow, onTurnDropped: drops.Add);
        var unboundItem = await unbound.FinishAsync(Turn("stale-unbound", 1), new DefaultHttpContext(), false);
        Assert.NotNull(unboundItem);
        var staleUnbound = new SessionCaptureItem(unboundItem.ClientSessionId, unboundItem.Turn, started);
        await writer.EnqueueAsync(new ServedRequest(Work("stale-unbound", 1), staleUnbound, unbound.NotifyDroppedOnce, started));

        ledger.Release.TrySetResult();
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        Assert.Empty(_store.ListTurns(_store.ResolveArchiveSessionId("stale-bound")));
        Assert.Equal(2, ledger.Calls);
        Assert.Equal(["stale-bound:1", "stale-unbound:1"], inserts);
        Assert.Single(drops);
        Assert.Equal(started, staleBound.Epoch);
        bound.Dispose();
        unbound.Dispose();
        pump.Dispose();
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>A cancelled request token does not skip the ledger or the live event. Both observe <see cref="CancellationToken.None"/>.</summary>
    [Fact]
    public async Task RequestAbort_DoesNotSkipTheLedgerOrTheLiveEvent()
    {
        var ledger = new GateLedger();
        var events = new List<CancellationToken>();
        using var writer = NewWriter(ledger: ledger, telemetry: events);
        await writer.StartAsync(CancellationToken.None);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers["X-Session-Id"] = "aborted";
        context.RequestAborted = new CancellationToken(canceled: true);
        await PublisherFor(writer).EnqueueServedAsync(
            writer: writer, gauge: null, capture: null, telemetryCaptureTruncated: false,
            context: context, route: Route(), requestedModelName: "m", isFallback: false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: """{"messages":[{"role":"user","content":"hi"}]}"""u8.ToArray(),
            capturedResponseBytes: """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":7}}"""u8.ToArray(),
            nativeResponseBytes: null, isStreaming: false, latencyToHeadersMs: 1, totalDurationMs: 2,
            statusCode: 200, upstreamHeaders: null, tailScanner: null, taskEmbedding: null, routerTokens: 0,
            resolutionReason: RoutingSubstitutionReason.None, isExploratory: false, propensity: 1,
            classification: null, taskText: "hi", dimBestModel: null, untrainedBaselineModel: null,
            untrainedBaselinePredictedScore: null);

        ledger.Release.TrySetResult();
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(CancellationToken.None, Assert.Single(ledger.Tokens));
        Assert.Equal(CancellationToken.None, Assert.Single(events));
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Finish runs before enqueue. Dispose after that does not release the spool. The writer does, and
    /// <see cref="TurnCapture.BindArchive"/> has already run.
    /// </summary>
    [Fact]
    public async Task Ownership_FinishBeforeEnqueue_WriterReleasesTheSpool()
    {
        var epoch = new CaptureEpoch();
        var pump = new CaptureBodyPump(Options.Create(new SessionCaptureOptions()), NullLogger<CaptureBodyPump>.Instance);
        var spool = SessionBodySpool.Create(_folder);
        Assert.True(spool.TryWrite("held"u8));
        Assert.True(spool.TryComplete());
        var capture = new TurnCapture(
            pump, null, epoch, () => SessionBodySpool.Create(_folder), NullLogger.Instance, DateTimeOffset.UtcNow,
            resolveArchiveSession: id => _store.ResolveArchiveSessionId(id));
        var binding = capture.BindArchive("owned");
        var finished = await capture.FinishAsync(Turn("owned", 1), new DefaultHttpContext(), false);
        Assert.NotNull(finished);
        Assert.Equal(binding.ArchiveTurnId, finished.Turn.ArchiveTurnId);

        var item = new SessionCaptureItem(
            finished.ClientSessionId,
            finished.Turn with
            {
                Bodies =
                [
                    new SessionBodyInput(SessionBodyKind.ClientResponse, null, spool),
                ],
            },
            finished.Epoch);
        capture.Dispose();
        Assert.NotEmpty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));

        using var writer = NewWriter();
        await writer.StartAsync(CancellationToken.None);
        await writer.EnqueueAsync(new ServedRequest(Work("owned", 1, resolve: _ => binding), item, capture.NotifyDroppedOnce, item.Epoch));
        Assert.True(await writer.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*" + SessionBodySpool.FileExtension));
        pump.Dispose();
        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary><see cref="ServedRequest"/> is built from the snapshot types and holds no HTTP context.</summary>
    [Fact]
    public void ServedRequest_HasNoHttpContext()
    {
        var served = new ServedRequest(Work("s", 1), null, null, startEpoch: 4);
        Assert.Equal(4, served.StartEpoch);
        Assert.DoesNotContain(
            typeof(ServedRequest).GetProperties(),
            property => property.PropertyType == typeof(HttpContext) || property.PropertyType == typeof(HttpResponseMessage));
        Assert.DoesNotContain(
            typeof(QueuedTelemetryWork).GetProperties(),
            property => property.PropertyType == typeof(HttpContext)
                || property.PropertyType == typeof(HttpResponseMessage)
                || property.PropertyType == typeof(HttpResponseHeaders));
    }

    private ServedRequestWriter NewWriter(
        GateLedger? ledger = null,
        ITranscriptStore? transcript = null,
        List<CancellationToken>? telemetry = null,
        ListLogger<ServedRequestWriter>? logger = null,
        ListLogger? publisherLogger = null,
        int capacity = 8,
        TimeSpan? drain = null,
        CaptureEpoch? epoch = null)
    {
        var publisher = new RequestTelemetryPublisher(
            logger: publisherLogger ?? (ILogger)NullLogger.Instance,
            sessionIdResolver: new SessionIdResolver(),
            continuityMatcher: new MessageHistoryContinuityMatcher(),
            turnTracker: new ConversationTurnTracker(),
            usageExtractor: new UsageExtractor(),
            responseTextExtractor: new ResponseTextExtractor(),
            telemetryPublisher: new TokenTelemetry(telemetry),
            qualityIngress: null,
            spendTracker: NullSpendTracker.Instance,
            priceLookup: null,
            budgetStore: null,
            usageLedger: ledger,
            pendingTaskEmbeddingCache: null,
            pendingRequestCostCache: null,
            pendingRequestProvenanceCache: null,
            pendingResponseTextCache: null,
            transcriptStore: transcript,
            routingOptionsMonitor: null,
            judgeOptionsMonitor: null,
            selfHostedRouterPricePerMillionTokens: 0.054m);
        var writer = new ServedRequestWriter(
            publisher,
            new Lazy<SessionStore>(() => _store),
            Options.Create(new SessionCaptureOptions
            {
                QueueCapacity = capacity,
                ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(2),
            }),
            logger ?? (ILogger<ServedRequestWriter>)NullLogger<ServedRequestWriter>.Instance,
            epoch);
        _publishers[writer] = publisher;
        return writer;
    }

    private readonly Dictionary<ServedRequestWriter, RequestTelemetryPublisher> _publishers = [];

    private RequestTelemetryPublisher PublisherFor(ServedRequestWriter writer)
    {
        if (_publishers.TryGetValue(writer, out var publisher)) return publisher;
        throw new InvalidOperationException("This writer was not built with a reachable publisher.");
    }

    private static ITranscriptStore RecordingTranscript(List<string> inserts)
    {
        var transcript = new Mock<ITranscriptStore>();
        transcript.Setup(store => store.InsertAsync(It.IsAny<TranscriptRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranscriptRecord record, CancellationToken _) =>
            {
                inserts.Add(record.CorrelationId);
                return 1L;
            });
        return transcript.Object;
    }

    private static SessionCaptureItem CaptureItem(string clientSessionId)
    {
        return new SessionCaptureItem(
            clientSessionId,
            new SessionTurnInput(Guid.CreateVersion7(), DateTimeOffset.UtcNow,
            [
                new SessionBodyInput(SessionBodyKind.ClientRequest, "request"u8.ToArray()),
            ]));
    }

    private static PublishedTurn Turn(string session, int turn) => Work(session, turn).Turn;

    private static QueuedTelemetryWork Work(string session, int turn, string? requestId = null,
        Func<string, ArchiveBinding>? resolve = null)
    {
        var published = new PublishedTurn(
            SessionId: session, TurnNumber: turn, IsSessionSynthesized: false, CorrelationId: $"{session}:{turn}",
            RequestedModel: "m", SubstitutionReason: RoutingSubstitutionReason.None, Provider: "openai",
            RoutedModel: "m", ProviderModelId: "m", IsFallback: false, IsExploratory: false, Propensity: 1,
            Classification: null, PromptTokens: 5, CompletionTokens: 7, CacheCreationTokens: null,
            CacheReadTokens: null, EstimatedCostUsd: 0.01m, CostConfidence: CostConfidence.Catalog,
            StatusCode: 200, LatencyToHeadersMs: 1, TotalDurationMs: 2, IsStreaming: false,
            NewestUserMessage: "q", ResponseText: "a");
        return new QueuedTelemetryWork(
            published, Route(), "m", "task", false, false, null, null, null, true, 0, "req", "res",
            requestId ?? $"{session}-{turn}", resolve);
    }

    private static ResolvedModelRoute Route() => new(
        ModelName: "m", Provider: "openai", ProviderModelId: "m",
        UpstreamBaseUrl: new Uri("https://example.test"), ExtraHeaders: [],
        ConfiguredHeaderNames: new HashSet<string>(), IsFree: true);

    /// <summary>Blocks inside <see cref="IUsageLedger.RecordAsync"/> until released, and can throw instead.</summary>
    private sealed class GateLedger : IUsageLedger
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> Tokens { get; } = [];

        public int Calls { get; private set; }

        public bool Throw { get; init; }

        /// <inheritdoc/>
        public async Task RecordAsync(UsageLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            Calls++;
            Tokens.Add(cancellationToken);
            if (Throw) throw new InvalidOperationException("ledger refused the row");

            if (Calls == 1) Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public int GetMaxTurnNumber(string sessionId) => 0;

        /// <inheritdoc/>
        public int DeleteOlderThan(DateTimeOffset cutoffUtc) => 0;

        /// <inheritdoc/>
        public decimal SumEstimatedCostUsd(string provider, DateTimeOffset fromUtc, DateTimeOffset toUtc) => 0;
    }

    /// <summary>Records the token each live event was published with.</summary>
    private sealed class TokenTelemetry : ITelemetryPublisher
    {
        private readonly List<CancellationToken>? _tokens;

        public TokenTelemetry(List<CancellationToken>? tokens) => _tokens = tokens;

        /// <inheritdoc/>
        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            _tokens?.Add(cancellationToken);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Keeps formatted log lines.</summary>
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Empty.Instance;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class Empty : IDisposable
        {
            public static readonly Empty Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>The same list logger, typed for the writer.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ListLogger _inner = new();

        public List<(LogLevel Level, string Message)> Entries => _inner.Entries;

        /// <summary>Waits until a warning with <paramref name="text"/> has been logged.</summary>
        /// <param name="text">The static template, or the start of the formatted line.</param>
        public async Task WaitForWarningAsync(string text)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (Entries.Exists(entry => entry.Level == LogLevel.Warning
                        && entry.Message.Contains(text, StringComparison.Ordinal)))
                    return;
                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException("The expected warning was not logged.");
        }

        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
