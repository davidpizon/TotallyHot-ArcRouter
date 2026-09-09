using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Characterization tests for <see cref="RequestTelemetryPublisher.PublishAsync"/>, written before the
/// method's internal decomposition (docs/router/code-smell-refactoring-plan.md M1) as a regression net -
/// this class previously had no dedicated unit tests at all. Exercises the real
/// <see cref="SessionIdResolver"/>, <see cref="MessageHistoryContinuityMatcher"/>,
/// <see cref="ConversationTurnTracker"/>, and <see cref="UsageExtractor"/> collaborators (all simple,
/// deterministic, and side-effect-free) alongside small hand-rolled fakes for the collaborators whose
/// calls need to be observed or made to fail (<see cref="FakeTelemetryPublisher"/>,
/// <see cref="FakeBudgetEnforcer"/>, <see cref="ThrowingTranscriptStore"/>).
/// </summary>
public class RequestTelemetryPublisherTests
{
    private const string SessionHeaderName = "x-claude-code-session-id";

    [Fact]
    public async Task PublishAsync_NativeUsagePresent_ExtractsUsageFromNativeBytesAndPublishesEvent()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var publisher = CreatePublisher(telemetryPublisher: telemetryPublisher);

        var route = CreateRoute(provider: "anthropic", true);
        var context = CreateContext(sessionId: "session-native");

        // Native (pre-translation) Anthropic bytes carry usage; the translated/client-shape capture
        // deliberately carries none, so a passing test proves the native bytes were actually the ones
        // consulted rather than incidentally falling through to the fallback path.
        var nativeBytes = """{"usage":{"input_tokens":11,"output_tokens":22}}"""u8.ToArray();
        var clientShapeBytes = """{"choices":[]}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            nativeResponseBytes: nativeBytes,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        var published = Assert.Single(telemetryPublisher.PublishedEvents);
        Assert.Equal(11, actual: published.PromptTokens);
        Assert.Equal(22, actual: published.CompletionTokens);
        Assert.Equal(0m, actual: published.EstimatedCostUsd);
        Assert.Equal(expected: CostConfidence.Exact, actual: published.CostConfidence);
    }

    [Fact]
    public async Task PublishAsync_NativeExtractionFails_FallsBackToClientShapeBytes()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var publisher = CreatePublisher(telemetryPublisher: telemetryPublisher);

        var route = CreateRoute(provider: "anthropic", true);
        var context = CreateContext(sessionId: "session-fallback");

        // Native bytes are non-empty but not valid usage-bearing JSON for the anthropic parser, so the
        // native attempt fails; the client-shape (openai-translated) bytes do carry usage and should be
        // what the fallback recovers.
        var nativeBytes = """{"not":"usage"}"""u8.ToArray();
        var clientShapeBytes =
            """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":7}}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            nativeResponseBytes: nativeBytes,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        var published = Assert.Single(telemetryPublisher.PublishedEvents);
        Assert.Equal(5, actual: published.PromptTokens);
        Assert.Equal(7, actual: published.CompletionTokens);
    }

    [Fact]
    public async Task PublishAsync_HeadCapturesFail_FallsBackToTailScanner()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var publisher = CreatePublisher(telemetryPublisher: telemetryPublisher);

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-tail");

        // Neither the (absent) native capture nor the head-capped client-shape capture carries usage -
        // simulating a large streamed response whose trailing usage event was cut off by the capture cap
        // - while the independently-retained tail window does.
        var clientShapeBytes = """{"choices":[]}"""u8.ToArray();
        var tailScanner = new IncrementalUsageScanner();
        tailScanner.Append(
            """{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":4}}"""u8);

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            null,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken,
            tailScanner: tailScanner);

        var published = Assert.Single(telemetryPublisher.PublishedEvents);
        Assert.Equal(3, actual: published.PromptTokens);
        Assert.Equal(4, actual: published.CompletionTokens);
    }

    [Fact]
    public async Task PublishAsync_UsageNotExtracted_DoesNotRecordToBudgetStore()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var budgetStore = new FakeBudgetEnforcer();
        var publisher = CreatePublisher(telemetryPublisher: telemetryPublisher, budgetStore: budgetStore);

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-no-usage");

        // No usage block anywhere: no native bytes, no tail scanner, and a client-shape body with none.
        var clientShapeBytes = """{"choices":[]}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            null,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(budgetStore.RecordedCalls);
        var published = Assert.Single(telemetryPublisher.PublishedEvents);
        Assert.Null(published.PromptTokens);
        Assert.Null(published.EstimatedCostUsd);
        Assert.Equal(expected: CostConfidence.NoUsage, actual: published.CostConfidence);
    }

    [Fact]
    public async Task PublishAsync_UsageExtracted_RecordsToBudgetStore()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var budgetStore = new FakeBudgetEnforcer();
        var publisher = CreatePublisher(telemetryPublisher: telemetryPublisher, budgetStore: budgetStore);

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-budget");

        var clientShapeBytes =
            """{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2}}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            null,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        var call = Assert.Single(budgetStore.RecordedCalls);
        Assert.Equal(expected: "openai", actual: call.ProviderKey);
        Assert.Equal(1, actual: call.PromptTokens);
        Assert.Equal(2, actual: call.CompletionTokens);
    }

    [Fact]
    public async Task PublishAsync_TranscriptStoreThrows_ExceptionIsSwallowed()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var transcriptStore = new ThrowingTranscriptStore();
        var routingOptionsMonitor =
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { EnableAdaptiveRouting = true });
        var publisher = CreatePublisher(
            telemetryPublisher: telemetryPublisher,
            transcriptStore: transcriptStore,
            routingOptionsMonitor: routingOptionsMonitor);

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-transcript");

        var clientShapeBytes = """{"choices":[]}"""u8.ToArray();

        // The transcript-store failure must not propagate out of PublishAsync - it is wrapped in its own
        // try/catch, matching every other best-effort telemetry side-effect on this path.
        var exception = await Record.ExceptionAsync(() => publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            null,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.True(transcriptStore.InsertAttempted);
        // The telemetry event still gets published even though the transcript write above failed.
        Assert.Single(telemetryPublisher.PublishedEvents);
    }

    [Fact]
    public async Task PublishAsync_RepresentativeCase_PublishesEventWithExpectedShape()
    {
        var telemetryPublisher = new FakeTelemetryPublisher();
        var publisher = CreatePublisher(telemetryPublisher: telemetryPublisher);

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-shape");

        var clientShapeBytes =
            """{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":50}}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "requested-model",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: "{}"u8.ToArray(),
            capturedResponseBytes: clientShapeBytes,
            null,
            false,
            42,
            99,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        var published = Assert.Single(telemetryPublisher.PublishedEvents);
        Assert.Equal(expected: "session-shape", actual: published.SessionId);
        Assert.Equal(1, actual: published.TurnNumber);
        Assert.False(published.IsSessionSynthesized);
        Assert.Equal(expected: "requested-model", actual: published.RequestedModel);
        Assert.Equal(expected: "openai", actual: published.Provider);
        Assert.Equal(100, actual: published.PromptTokens);
        Assert.Equal(50, actual: published.CompletionTokens);
        Assert.Equal(0m, actual: published.EstimatedCostUsd);
        Assert.Equal(200, actual: published.StatusCode);
        Assert.Equal(expected: "session-shape:1", actual: published.CorrelationId);
    }

    // -- Phase Q3: the retention gate is "any LLM grader is live", not just the judge -----------------

    /// <summary>
    /// Covers <c>RequestTelemetryPublisher.AnyLlmGraderEnabled</c> - the single authorization check for
    /// retaining raw prompt/response text in <see cref="PendingResponseTextCache"/>/<see cref="PendingPromptCache"/>
    /// at all. Before Phase Q3 this was <c>JudgeOptions.Enabled</c> alone; Q3's CodeJudge/ICE-Score/RACE
    /// portfolio also needs that same cached text, so retention must now also fire when a portfolio grader
    /// is live even if the G-Eval judge itself is off - and, symmetrically, stay suppressed only when
    /// *neither* is live.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, false, false)]
    public async Task PublishAsync_RetentionGate_FiresWheneverAnyLlmGraderIsLive(
        bool judgeEnabled, bool codeJudgeEnabled, bool iceScoreEnabled, bool raceEnabled, bool expectRetained)
    {
        var responseTextCache = new PendingResponseTextCache(Options.Create(new JudgeOptions()));
        var promptCache = new PendingPromptCache(Options.Create(new JudgeOptions()));
        var publisher = CreatePublisher(
            pendingResponseTextCache: responseTextCache,
            pendingPromptCache: promptCache,
            judgeOptionsMonitor: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = judgeEnabled }),
            portfolioGraderOptionsMonitor: new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
            {
                CodeJudgeEnabled = codeJudgeEnabled,
                IceScoreEnabled = iceScoreEnabled,
                RaceEnabled = raceEnabled
            }));

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-retention");
        var requestBody = """{"messages":[{"role":"user","content":"the question"}]}"""u8.ToArray();
        var responseBody = """{"choices":[{"message":{"content":"the answer"}}]}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: requestBody,
            capturedResponseBytes: responseBody,
            null,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        // Turn 1 of a freshly-tracked session, matching PublishAsync_RepresentativeCase's own correlation-id shape.
        const string correlationId = "session-retention:1";
        Assert.Equal(expected: expectRetained, actual: responseTextCache.TryPeek(correlationId: correlationId, text: out _));
        Assert.Equal(expected: expectRetained, actual: promptCache.TryPeek(correlationId: correlationId, prompt: out _));
    }

    [Fact]
    public async Task PublishAsync_NoOptionsMonitorsSupplied_DoesNotRetainResponseTextOrPrompt()
    {
        var responseTextCache = new PendingResponseTextCache(Options.Create(new JudgeOptions()));
        var promptCache = new PendingPromptCache(Options.Create(new JudgeOptions()));
        // Neither judgeOptionsMonitor nor portfolioGraderOptionsMonitor supplied - both null, exactly as
        // a host that has not wired either up would leave them (ProxyMiddlewareDependencies' documented
        // "null means not enabled" default for both).
        var publisher = CreatePublisher(pendingResponseTextCache: responseTextCache, pendingPromptCache: promptCache);

        var route = CreateRoute(provider: "openai", true);
        var context = CreateContext(sessionId: "session-no-monitors");
        var requestBody = """{"messages":[{"role":"user","content":"the question"}]}"""u8.ToArray();
        var responseBody = """{"choices":[{"message":{"content":"the answer"}}]}"""u8.ToArray();

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: requestBody,
            capturedResponseBytes: responseBody,
            null,
            false,
            10,
            20,
            200,
            cancellationToken: TestContext.Current.CancellationToken);

        const string correlationId = "session-no-monitors:1";
        Assert.False(responseTextCache.TryPeek(correlationId: correlationId, text: out _));
        Assert.False(promptCache.TryPeek(correlationId: correlationId, prompt: out _));
    }

    // -- helpers -------------------------------------------------------------

    private static RequestTelemetryPublisher CreatePublisher(
        FakeTelemetryPublisher? telemetryPublisher = null,
        IBudgetEnforcer? budgetStore = null,
        ITranscriptStore? transcriptStore = null,
        StaticOptionsMonitor<RoutingOptions>? routingOptionsMonitor = null,
        PendingResponseTextCache? pendingResponseTextCache = null,
        PendingPromptCache? pendingPromptCache = null,
        IOptionsMonitor<JudgeOptions>? judgeOptionsMonitor = null,
        IOptionsMonitor<PortfolioGraderOptions>? portfolioGraderOptionsMonitor = null)
    {
        return new RequestTelemetryPublisher(
            logger: NullLogger.Instance,
            sessionIdResolver: new SessionIdResolver(),
            continuityMatcher: new MessageHistoryContinuityMatcher(),
            turnTracker: new ConversationTurnTracker(),
            usageExtractor: new UsageExtractor(),
            responseTextExtractor: new ResponseTextExtractor(),
            telemetryPublisher: telemetryPublisher ?? new FakeTelemetryPublisher(),
            qualityIngress: null,
            spendTracker: NullSpendTracker.Instance,
            priceLookup: null,
            budgetStore: budgetStore,
            usageLedger: null,
            pendingTaskEmbeddingCache: null,
            pendingRequestCostCache: null,
            pendingRequestProvenanceCache: null,
            pendingResponseTextCache: pendingResponseTextCache,
            transcriptStore: transcriptStore,
            routingOptionsMonitor: routingOptionsMonitor,
            judgeOptionsMonitor: judgeOptionsMonitor,
            selfHostedRouterPricePerMillionTokens: 0.054m,
            pendingPromptCache: pendingPromptCache,
            portfolioGraderOptionsMonitor: portfolioGraderOptionsMonitor);
    }

    private static ResolvedModelRoute CreateRoute(string provider, bool isFree)
    {
        return new ResolvedModelRoute(
            ModelName: "primary",
            Provider: provider,
            ProviderModelId: "provider-model-id",
            UpstreamBaseUrl: new Uri("https://example.test"),
            AuthHeaderName: "Authorization",
            ExtraHeaders: [],
            IsFree: isFree);
    }

    private static HttpContext CreateContext(string sessionId)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers[SessionHeaderName] = sessionId;
        return context;
    }

    /// <summary>Captures every <see cref="RoutingTelemetryEvent"/> published, for assertion.</summary>
    private sealed class FakeTelemetryPublisher : ITelemetryPublisher
    {
        public List<RoutingTelemetryEvent> PublishedEvents { get; } = [];

        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            PublishedEvents.Add(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Records every <see cref="RecordUsageAsync"/> call, for gating assertions. Never reports a breach.</summary>
    private sealed class FakeBudgetEnforcer : IBudgetEnforcer
    {
        public List<RecordedCall> RecordedCalls { get; } = [];

        public bool IsBreached(string providerKey)
        {
            return false;
        }

        public Task RecordUsageAsync(
            string providerKey,
            decimal? costUsd,
            int? promptTokens,
            int? completionTokens,
            int? cacheCreationTokens,
            int? cacheReadTokens,
            DateTimeOffset usageAtUtc,
            CancellationToken cancellationToken = default)
        {
            RecordedCalls.Add(new RecordedCall(ProviderKey: providerKey, CostUsd: costUsd, PromptTokens: promptTokens,
                CompletionTokens: completionTokens));
            return Task.CompletedTask;
        }

        public sealed record RecordedCall(
            string ProviderKey,
            decimal? CostUsd,
            int? PromptTokens,
            int? CompletionTokens);
    }

    /// <summary>
    /// Always throws from <see cref="InsertAsync"/>, to verify <c>PublishAsync</c> swallows the failure.
    /// Every other member throws <see cref="NotSupportedException"/> since <c>PublishAsync</c> only ever
    /// calls <see cref="InsertAsync"/> on this seam.
    /// </summary>
    private sealed class ThrowingTranscriptStore : ITranscriptStore
    {
        public bool InsertAttempted { get; private set; }

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            InsertAttempted = true;
            throw new InvalidOperationException("simulated transcript store failure");
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
    }
}