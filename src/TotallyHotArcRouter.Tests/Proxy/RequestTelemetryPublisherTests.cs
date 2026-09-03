using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
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
        var nativeBytes = Encoding.UTF8.GetBytes("""{"usage":{"input_tokens":11,"output_tokens":22}}""");
        var clientShapeBytes = Encoding.UTF8.GetBytes("""{"choices":[]}""");

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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
        var nativeBytes = Encoding.UTF8.GetBytes("""{"not":"usage"}""");
        var clientShapeBytes =
            Encoding.UTF8.GetBytes("""{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":7}}""");

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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
        var clientShapeBytes = Encoding.UTF8.GetBytes("""{"choices":[]}""");
        var tailScanner = new IncrementalUsageScanner();
        tailScanner.Append(
            Encoding.UTF8.GetBytes("""{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":4}}"""));

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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
        var clientShapeBytes = Encoding.UTF8.GetBytes("""{"choices":[]}""");

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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
            Encoding.UTF8.GetBytes("""{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2}}""");

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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

        var clientShapeBytes = Encoding.UTF8.GetBytes("""{"choices":[]}""");

        // The transcript-store failure must not propagate out of PublishAsync - it is wrapped in its own
        // try/catch, matching every other best-effort telemetry side-effect on this path.
        var exception = await Record.ExceptionAsync(() => publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "primary",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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
            Encoding.UTF8.GetBytes("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":50}}""");

        await publisher.PublishAsync(
            context: context,
            route: route,
            requestedModelName: "requested-model",
            false,
            telemetryShapeProvider: "openai",
            rewrittenRequestBody: Encoding.UTF8.GetBytes("{}"),
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

    // -- helpers -------------------------------------------------------------

    private static RequestTelemetryPublisher CreatePublisher(
        FakeTelemetryPublisher? telemetryPublisher = null,
        IBudgetEnforcer? budgetStore = null,
        ITranscriptStore? transcriptStore = null,
        StaticOptionsMonitor<RoutingOptions>? routingOptionsMonitor = null)
    {
        return new RequestTelemetryPublisher(
            logger: NullLogger.Instance,
            sessionIdResolver: new SessionIdResolver(),
            continuityMatcher: new MessageHistoryContinuityMatcher(),
            turnTracker: new ConversationTurnTracker(),
            usageExtractor: new UsageExtractor(),
            responseTextExtractor: new ResponseTextExtractor(),
            telemetryPublisher: telemetryPublisher ?? new FakeTelemetryPublisher(),
            null,
            spendTracker: NullSpendTracker.Instance,
            null,
            budgetStore: budgetStore,
            null,
            null,
            null,
            null,
            null,
            transcriptStore: transcriptStore,
            routingOptionsMonitor: routingOptionsMonitor,
            null,
            0.054m);
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