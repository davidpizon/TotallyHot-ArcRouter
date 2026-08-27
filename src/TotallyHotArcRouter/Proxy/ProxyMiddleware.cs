using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Quality.Ingress;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Net.Sockets;
using System.Collections.Generic;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Middleware for handling and forwarding proxy requests.
/// </summary>
public class ProxyMiddleware : IMiddleware, IDisposable
{
    // RFC 7230 Section 6.1 hop-by-hop headers: meaningful only for a single transport-level connection,
    // so they must never be blindly forwarded between the client, this proxy, and the upstream.
    private static readonly string[] HopByHopHeaders =
    [
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    ];

    // "Authorization" carries the client's inbound credential to the proxy itself (e.g. a placeholder
    // token an IDE/BYOK client requires but never validates), not a credential for the upstream provider.
    // It must never be forwarded as-is: for providers whose AuthHeaderName is something else (e.g.
    // Anthropic's "x-api-key"), forwarding it would send the client's bogus token to the upstream
    // alongside the real injected credential, and some providers reject the request outright when both
    // are present.
    //
    // "Accept-Encoding" is skipped because _httpClient never configures AutomaticDecompression: if the
    // client's own "Accept-Encoding: gzip" were relayed upstream, a provider that honors it would send a
    // gzip-compressed body that this proxy reads (and, for a translated provider, re-parses as plain-text
    // SSE/JSON) without ever decompressing it - producing either a translation failure or, worse, a
    // successful-looking response whose Content-Encoding header (copied from upstream) is a lie once the
    // body has been rewritten by a translator, which the client then fails to gunzip (zlib
    // "incorrect header check"). Not asking upstream to compress at all sidesteps both failure modes.
    private static readonly string[] AlwaysSkippedRequestHeaders = ["Host", "Content-Type", "Content-Length", "Authorization", "Accept-Encoding"];

    // The OpenAI-compatible model discovery path. Answered locally from configuration (mirroring LiteLLM's
    // /v1/models behavior) since it has no request body to resolve a single upstream provider from, and no
    // single upstream to forward it to anyway when ModelList spans multiple providers.
    private const string ModelsListPath = "/v1/models";

    // Ollama's native model discovery path. A client that adds this proxy as an "Ollama" provider (e.g.
    // Visual Studio's AI model picker) probes this GET endpoint - with no body - to list models, exactly
    // like ModelsListPath above but in Ollama's own response shape rather than OpenAI's. Answered the same
    // way: locally from configuration, never forwarded, since there is no body to resolve a single upstream
    // from and no single upstream anyway when ModelList spans multiple providers.
    private const string OllamaTagsPath = "/api/tags";

    // Ollama's native per-model detail path. A client that discovers models via OllamaTagsPath above (e.g.
    // Visual Studio's AI model picker) follows up with one POST here per model to fetch its details before
    // use. Answered locally from configuration, exactly like OllamaTagsPath: without this, the request falls
    // through to the normal per-model routing path, which resolves its {"model": "..."} body to a real
    // upstream candidate and forwards it there verbatim - a malformed chat/completion request that the
    // upstream (correctly) rejects, surfacing as a confusing 400 with no indication /api/show was involved.
    private const string OllamaShowPath = "/api/show";

    // Cap on how much of the response body telemetry captures for usage parsing (see CopyAndCaptureAsync).
    // Real chat/completion responses are almost always well under this; a response that exceeds it just
    // means usage parsing has less to work with (a truncated/partial buffer that the usage parsers already
    // handle gracefully by finding nothing), never a failure of the actual client-facing forward, which is
    // unaffected by this cap - every byte is still copied to the client regardless.
    private const int MaxCapturedResponseBytes = 4 * 1024 * 1024;

    private readonly ILogger<ProxyMiddleware> _logger;
    private readonly HttpClient _httpClient;
    private readonly RequestInterceptor _interceptor;
    private readonly ISessionIdResolver _sessionIdResolver;
    private readonly IConversationContinuityMatcher _continuityMatcher;
    private readonly IConversationTurnTracker _turnTracker;
    private readonly IUsageExtractor _usageExtractor;
    private readonly IResponseTextExtractor _responseTextExtractor;
    private readonly ITelemetryPublisher _telemetryPublisher;
    private readonly IQualityIngress? _qualityIngress;
    private readonly ISpendTracker _spendTracker;
    private readonly IModelPriceLookup? _priceLookup;
    private readonly IReadOnlyDictionary<string, IPayloadTranslator> _translators;
    private readonly IBedrockRuntimeClientFactory _bedrockClientFactory;
    private readonly PriceCatalog.IBudgetEnforcer? _budgetStore;
    private readonly IUsageLedger? _usageLedger;
    private readonly IRateLimitHeaderCapture _rateLimitCapture;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly ToolCallNormalizerFactory _toolCallNormalizerFactory;
    private readonly Router.Embeddings.PendingTaskEmbeddingCache? _pendingTaskEmbeddingCache;
    private readonly PendingRequestCostCache? _pendingRequestCostCache;
    private readonly PendingRequestProvenanceCache? _pendingRequestProvenanceCache;
    private readonly PendingResponseTextCache? _pendingResponseTextCache;
    private readonly ITranscriptStore? _transcriptStore;
    private readonly InFlightRequestGauge? _inFlightGauge;
    private readonly IOptionsMonitor<Models.RoutingOptions>? _routingOptionsMonitor;
    private readonly IOptionsMonitor<Judge.JudgeOptions>? _judgeOptionsMonitor;
    private readonly Router.IRoutingGate? _routingGate;

    // The rate the router's own tokens are charged at (see RoutingOptions.SelfHostedRouterPricePerMillionTokens).
    // Read once at construction rather than per request: it is a static amortization figure, not a catalog
    // price that a background ingestion cycle can refresh underneath us.
    private readonly decimal _selfHostedRouterPricePerMillionTokens;

    // True only when no factory was supplied and this instance built its own fallback - in that case
    // ProxyMiddleware is the sole owner of that factory's lifetime and must dispose it (it caches AWS SDK
    // clients and implements IDisposable; see BedrockRuntimeClientFactory's remarks). When a factory is
    // supplied (the real app's DI-registered singleton), its lifetime belongs to whoever registered it -
    // disposing it here would pull it out from under other consumers of the same DI-owned instance.
    private readonly bool _ownsBedrockClientFactory;

    private static readonly IReadOnlyDictionary<string, IPayloadTranslator> NoTranslators =
        new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Response header carrying the client's literal requested model (docs/router/orchestrator-live-path-plan.md §M2.2).</summary>
    private const string RequestedModelHeaderName = "X-ArcRouter-Requested-Model";

    /// <summary>Response header carrying the model that actually served the request.</summary>
    private const string RoutedModelHeaderName = "X-ArcRouter-Routed-Model";

    /// <summary>Response header carrying the <see cref="RoutingSubstitutionReason"/> for why the two headers above differ, if they do.</summary>
    private const string SubstitutionReasonHeaderName = "X-ArcRouter-Substitution-Reason";

    /// <summary>
    /// The result of capturing a translated response for telemetry: the OpenAI-shaped bytes actually sent
    /// to the client, and - only for a provider <see cref="UsageExtractor.SupportsNativeShape"/> recognizes
    /// - a second, capped copy of the pre-translation native bytes, immune to translation lossiness
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4). <see cref="NativeBytes"/> is
    /// <see langword="null"/> for a pass-through (no translator ran, so the client bytes already are native)
    /// or an unsupported translated provider (e.g. Gemini). <see cref="TailScanner"/> retained a trailing
    /// window over the client-shape stream, independent of <see cref="ClientShapeBytes"/>'s head cap -
    /// consulted by <c>PublishTelemetryAsync</c> only when usage extraction fails against both the
    /// head-capped and native captures (§5.11).
    /// </summary>
    private readonly record struct CapturedResponse(byte[] ClientShapeBytes, byte[]? NativeBytes, IncrementalUsageScanner? TailScanner);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyMiddleware"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="interceptor">Request/response interceptor.</param>
    /// <param name="httpClient">Optional HTTP client used for forwarding requests.</param>
    /// <param name="sessionIdResolver">Optional session-id resolver; defaults to <see cref="SessionIdResolver"/>.</param>
    /// <param name="continuityMatcher">Optional continuity matcher, used when <paramref name="sessionIdResolver"/> finds nothing; defaults to a fresh <see cref="MessageHistoryContinuityMatcher"/> private to this instance.</param>
    /// <param name="turnTracker">Optional turn tracker; defaults to a fresh <see cref="ConversationTurnTracker"/> private to this instance.</param>
    /// <param name="usageExtractor">Optional usage extractor; defaults to <see cref="UsageExtractor"/>.</param>
    /// <param name="responseTextExtractor">Optional response-text extractor; defaults to <see cref="ResponseTextExtractor"/>.</param>
    /// <param name="telemetryPublisher">Optional telemetry publisher; defaults to a fresh <see cref="TelemetryPublisher"/> backed by a private, unshared <see cref="TelemetryBroadcaster"/> (a safe no-op, since nothing is ever registered to receive from it).</param>
    /// <param name="qualityIngress">Optional quality-verifier ingress façade; when supplied, completed responses are enqueued for off-path grading. Best-effort and non-blocking; defaults to <see langword="null"/> (disabled).</param>
    /// <param name="spendTracker">Optional running-spend tracker; defaults to <see cref="NullSpendTracker"/> (a safe no-op) so existing callers/tests that don't need it are unaffected.</param>
    /// <param name="priceLookup">Optional catalog price lookup (docs/router/model-price-catalog.md); when supplied, a paid route's per-request cost is estimated from the auto-refreshed price catalog. Defaults to <see langword="null"/> (disabled), leaving paid-model cost unknown as before.</param>
    /// <param name="translators">Optional per-provider payload translators (docs/router/unified-api-translation.md), keyed by provider name. A provider present here has its request/response/stream translated to and from OpenAI's shape (Gemini and Bedrock providers always; Anthropic when <see cref="IPayloadTranslator.ShouldTranslate"/> allows it for the request); a provider absent here, or whose translator vetoes this request, is forwarded byte-for-byte, exactly as before. Defaults to an empty map (all providers pass through unchanged).</param>
    /// <param name="bedrockClientFactory">Optional factory for the Amazon Bedrock Runtime SDK client used by any translator implementing <see cref="IBedrockPayloadTranslator"/>. In the real app this is always supplied via DI (a shared singleton the container owns and disposes); when omitted (direct construction outside DI, e.g. a caller that never touches the Bedrock path), this instance builds and owns its own <see cref="BedrockRuntimeClientFactory"/> and disposes it in <see cref="Dispose"/>. Overridable for tests, which substitute a fake <c>IAmazonBedrockRuntime</c> so no live AWS call is made.</param>
    /// <param name="budgetStore">Optional per-provider monthly budget store (Governance &gt; Providers). When supplied, a provider whose cap is exhausted is skipped for the request, an all-breached request is rejected with 402, and each served request's usage is recorded against the serving provider. Defaults to <see langword="null"/> (no budgets enforced or recorded), so existing callers/tests are unaffected.</param>
    /// <param name="circuitBreaker">Optional per-upstream-target circuit breaker (<c>docs/router/agent-resilience-strategies.md</c>). Must be the <em>same</em> instance given to the <see cref="RequestInterceptor"/> this middleware wraps (see <c>ServiceCollectionExtensions</c>'s DI wiring) - this class is what records the successes/failures <see cref="RequestInterceptor"/> reads back when ranking candidates. Defaults to a fresh, always-CLOSED instance when omitted, which is behaviorally inert (existing callers/tests unaffected) but decoupled from any interceptor-side instance, so circuit state recorded here would never be seen there.</param>
    /// <param name="toolCallNormalizerFactory">Optional per-request tool-call normalization (<c>docs/router/tool-call-normalization.md</c> Phase 4), consulted for any candidate with no other translator registered: it decides from the (provider, model) capability row and whether the request carried <c>tools</c> whether the response needs a dialect scan at all, and rewrites a dialect-framed tool call into a real <c>tool_calls</c> shape. Replaces the provider-wide echo guard of <c>unified-api-translation.md</c> §4.5. In the real app this is always supplied via DI (a shared singleton reading the capability store); when omitted (direct construction outside DI), this instance builds a store-less one - so a tools-carrying request is still normalized with the union of dialects, but nothing is classified or persisted, mirroring <paramref name="circuitBreaker"/>'s "behaviorally inert when defaulted" pattern.</param>
    /// <param name="rateLimitCapture">Optional capture for upstream <c>anthropic-ratelimit-*</c> response headers (<c>docs/router/anthropic-reported-usage-plan.md</c> §5), invoked as soon as each attempt's response headers arrive. Defaults to a no-op, so existing callers/tests are unaffected.</param>
    /// <param name="usageLedger">Optional durable usage ledger (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2), recorded to immediately after <paramref name="budgetStore"/> on the request path. When <see langword="null"/> (e.g. tests constructing this type directly), no ledger row is written - the rest of telemetry is unaffected.</param>
    /// <param name="pendingTaskEmbeddingCache">
    /// Optional bridge (docs/router/live-feedback-learning-plan.md Phase 2c) between
    /// <see cref="RequestInterceptor"/>'s Phase 2b embedding computation and the request's
    /// later-arriving verifier score, which is only correlated by the id computed below alongside session
    /// and turn resolution - the earliest point that id is actually known, since
    /// <see cref="RequestInterceptor.ResolveModelRouteAsync"/> runs before it. Defaults to
    /// <see langword="null"/> (no entries recorded), so existing callers/tests are unaffected.
    /// </param>
    /// <param name="routingOptions">
    /// Supplies <see cref="Models.RoutingOptions.SelfHostedRouterPricePerMillionTokens"/>, the rate the
    /// router's own token consumption is charged at when published on
    /// <see cref="Telemetry.RoutingTelemetryEvent.RouterCostUsd"/>. When <see langword="null"/> (direct
    /// construction outside DI) the compiled-in default applies, so the figure is still real rather than
    /// suppressed - unlike the optional collaborators above, there is no "unavailable" state for a static
    /// amortization rate.
    /// </param>
    /// <param name="pendingRequestCostCache">
    /// Optional bridge (docs/router/self-organizing-classification-plan.md Phase T1c) between this
    /// request's estimated cost, computed below, and its later-arriving verifier score - mirrors
    /// <paramref name="pendingTaskEmbeddingCache"/>'s role exactly, for a different value. Defaults to
    /// <see langword="null"/> (no entries recorded), so existing callers/tests are unaffected.
    /// </param>
    /// <param name="pendingRequestProvenanceCache">
    /// Optional bridge (docs/router/self-organizing-classification-plan.md Phase T1c) between this
    /// request's exploration provenance (is-exploratory/propensity, resolved earlier by
    /// <see cref="RequestInterceptor"/>) and its later-arriving verifier score. Defaults to
    /// <see langword="null"/> (no entries recorded), so existing callers/tests are unaffected.
    /// </param>
    /// <param name="pendingResponseTextCache">
    /// Optional bridge (docs/router/geval-shadow-scoring-plan.md §Raw-text preservation) between this
    /// request's already-extracted response text and the shadow judge's later-arriving background job -
    /// mirrors <paramref name="pendingTaskEmbeddingCache"/>'s role exactly, for a different value.
    /// Populated at the same point <paramref name="responseTextExtractor"/>'s result is already in hand, so
    /// this adds retention, not parsing. Defaults to <see langword="null"/> (no entries recorded), so
    /// existing callers/tests are unaffected.
    /// </param>
    /// <param name="transcriptStore">
    /// Optional opt-in transcript store (docs/router/self-organizing-classification-plan.md Phase T1a/T1b).
    /// When supplied and transcript capture is enabled, one row is inserted per served request with the
    /// prompt/response text, classification, cost, and provenance already in scope at this point; the
    /// score is backfilled later by <see cref="Transcripts.TranscriptScoreObserver"/>. Defaults to
    /// <see langword="null"/> (no rows written), matching the feature's opt-in-and-off-by-default posture.
    /// </param>
    /// <param name="inFlightGauge">
    /// Optional in-flight request gauge (docs/router/routing-roi-regret-plan.md). When supplied, every
    /// request is counted for the full duration of <see cref="InvokeAsync"/> so background analysis work
    /// (the taxonomy-comparison drain) can hard-pause while traffic is being served. Defaults to
    /// <see langword="null"/> (no tracking), so existing callers/tests are unaffected.
    /// </param>
    /// <param name="routingOptionsMonitor">
    /// Optional live routing-options monitor (docs/router/self-organizing-classification-plan.md Phase T6),
    /// consulted alongside <see cref="Transcripts.TranscriptOptions.Enabled"/> at the transcript-insert
    /// site so a <see cref="Models.RoutingOptions.EnableAdaptiveRouting"/> toggle stops (or resumes) new
    /// transcript writes without a restart. Defaults to <see langword="null"/>, which is treated as
    /// adaptive routing being disabled - matching <see cref="Models.RoutingOptions.EnableAdaptiveRouting"/>'s
    /// own off-by-default coded value.
    /// </param>
    /// <param name="judgeOptionsMonitor">
    /// Optional live shadow-judge options monitor, consulted at the response-text retention site so raw
    /// response text is held for judging only while <see cref="Judge.JudgeOptions.Enabled"/> is actually
    /// on. Read live rather than captured because that flag is operator-toggleable at runtime, and this is
    /// the gate that decides whether raw text is retained at all - it must go off the moment the operator
    /// says so, not at the next restart. Defaults to <see langword="null"/>, treated as the judge being
    /// disabled, matching <see cref="Judge.JudgeOptions.Enabled"/>'s own off-by-default coded value.
    /// </param>
    /// <param name="routingGate">
    /// Optional runtime kill switch, toggled from the GUI system tray via
    /// <see cref="Router.RoutingGateAdminGrpcService"/>. When <see cref="Router.IRoutingGate.IsEnabled"/> is
    /// <see langword="false"/>, every LLM-forwarding request is rejected with 503 before routing is
    /// attempted; <see langword="null"/> (the default) means routing is always accepted, matching the
    /// enabled-by-default coded value <see cref="Router.RoutingGateStore"/> itself falls back to.
    /// </param>
    public ProxyMiddleware(
        ILogger<ProxyMiddleware> logger,
        RequestInterceptor interceptor,
        HttpClient? httpClient = null,
        ISessionIdResolver? sessionIdResolver = null,
        IConversationContinuityMatcher? continuityMatcher = null,
        IConversationTurnTracker? turnTracker = null,
        IUsageExtractor? usageExtractor = null,
        IResponseTextExtractor? responseTextExtractor = null,
        ITelemetryPublisher? telemetryPublisher = null,
        IQualityIngress? qualityIngress = null,
        ISpendTracker? spendTracker = null,
        IModelPriceLookup? priceLookup = null,
        IReadOnlyDictionary<string, IPayloadTranslator>? translators = null,
        IBedrockRuntimeClientFactory? bedrockClientFactory = null,
        PriceCatalog.IBudgetEnforcer? budgetStore = null,
        ICircuitBreaker? circuitBreaker = null,
        ToolCallNormalizerFactory? toolCallNormalizerFactory = null,
        IRateLimitHeaderCapture? rateLimitCapture = null,
        IUsageLedger? usageLedger = null,
        Router.Embeddings.PendingTaskEmbeddingCache? pendingTaskEmbeddingCache = null,
        IOptions<Models.RoutingOptions>? routingOptions = null,
        PendingRequestCostCache? pendingRequestCostCache = null,
        PendingRequestProvenanceCache? pendingRequestProvenanceCache = null,
        PendingResponseTextCache? pendingResponseTextCache = null,
        ITranscriptStore? transcriptStore = null,
        InFlightRequestGauge? inFlightGauge = null,
        IOptionsMonitor<Models.RoutingOptions>? routingOptionsMonitor = null,
        IOptionsMonitor<Judge.JudgeOptions>? judgeOptionsMonitor = null,
        Router.IRoutingGate? routingGate = null)
    {
        _logger = logger;
        _interceptor = interceptor;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        _sessionIdResolver = sessionIdResolver ?? new SessionIdResolver();
        _continuityMatcher = continuityMatcher ?? new MessageHistoryContinuityMatcher();
        _turnTracker = turnTracker ?? new ConversationTurnTracker();
        _usageExtractor = usageExtractor ?? new UsageExtractor();
        _responseTextExtractor = responseTextExtractor ?? new ResponseTextExtractor();
        _telemetryPublisher = telemetryPublisher ?? new TelemetryPublisher(new TelemetryBroadcaster());
        _qualityIngress = qualityIngress;
        _spendTracker = spendTracker ?? NullSpendTracker.Instance;
        _priceLookup = priceLookup;
        _translators = translators ?? NoTranslators;
        _budgetStore = budgetStore;
        _usageLedger = usageLedger;
        _rateLimitCapture = rateLimitCapture ?? NullRateLimitHeaderCapture.Instance;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _toolCallNormalizerFactory = toolCallNormalizerFactory ?? new ToolCallNormalizerFactory();
        _pendingTaskEmbeddingCache = pendingTaskEmbeddingCache;
        _pendingRequestCostCache = pendingRequestCostCache;
        _pendingRequestProvenanceCache = pendingRequestProvenanceCache;
        _pendingResponseTextCache = pendingResponseTextCache;
        _transcriptStore = transcriptStore;
        _inFlightGauge = inFlightGauge;
        _routingOptionsMonitor = routingOptionsMonitor;
        _judgeOptionsMonitor = judgeOptionsMonitor;
        _routingGate = routingGate;
        _selfHostedRouterPricePerMillionTokens =
            routingOptions?.Value.SelfHostedRouterPricePerMillionTokens ?? new Models.RoutingOptions().SelfHostedRouterPricePerMillionTokens;

        if (bedrockClientFactory is null)
        {
            _bedrockClientFactory = new BedrockRuntimeClientFactory();
            _ownsBedrockClientFactory = true;
        }
        else
        {
            _bedrockClientFactory = bedrockClientFactory;
        }
    }

    /// <summary>
    /// Disposes the self-owned fallback <see cref="BedrockRuntimeClientFactory"/> when this instance
    /// created one (see <see cref="_ownsBedrockClientFactory"/>'s remarks) - a no-op when a factory was
    /// supplied, since that one's lifetime belongs to its own owner (the DI container in the real app).
    /// <see cref="ProxyMiddleware"/> is itself a DI-registered singleton, so the container invokes this
    /// at shutdown the same way it would for any other disposable singleton.
    /// </summary>
    public void Dispose()
    {
        if (_ownsBedrockClientFactory && _bedrockClientFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // The gauge scope spans the entire request - routing, upstream call, and response streaming -
        // because background work pausing on "in flight" (see InFlightRequestGauge) must stay paused
        // for exactly as long as a client could feel the contention.
        using var _ = _inFlightGauge?.Track();
        await InvokeCoreAsync(context, next);
    }

    /// <summary>
    /// The whole of the request pipeline behind <see cref="InvokeAsync"/>, split out so the in-flight
    /// accounting above wraps every exit path (including exceptions and streamed responses) in one
    /// <c>using</c> scope rather than threading try/finally through this method's many returns.
    /// </summary>
    /// <param name="context">The HTTP context being served.</param>
    /// <param name="next">The next middleware delegate (unused; the proxy is terminal).</param>
    private async Task InvokeCoreAsync(HttpContext context, RequestDelegate next)
    {
        _logger.LogInformation("Proxy middleware caught request to {Path}", SanitizeForLog(context.Request.Path.ToString()));

        if (IsModelsListRequest(context.Request))
        {
            await WriteModelsListResponseAsync(context);
            return;
        }

        if (IsOllamaTagsRequest(context.Request))
        {
            await WriteOllamaTagsResponseAsync(context);
            return;
        }

        if (IsOllamaShowRequest(context.Request))
        {
            await WriteOllamaShowResponseAsync(context);
            return;
        }

        // The GUI system tray's "Disable Routing" kill switch: checked after the read-only /v1/models and
        // Ollama listing endpoints above (which stay available so clients can still discover models while
        // routing is paused) but before any actual routing/forwarding work begins. Every other
        // admin/management surface (REST /admin/*, the gRPC admin services, and this same kill switch's own
        // toggle RPC) lives on separate endpoints mapped ahead of this terminal middleware, so disabling
        // routing never blocks administrative tasks.
        if (_routingGate?.IsEnabled == false)
        {
            await WriteRoutingDisabledResponseAsync(context);
            return;
        }

        await _interceptor.InterceptRequestAsync(context);

        var resolution = await _interceptor.ResolveModelRouteAsync(context, context.RequestAborted);

        if (!resolution.IsSuccess)
        {
            await WriteModelNotFoundResponseAsync(context, resolution.ErrorMessage!);
            return;
        }

        var candidates = resolution.Candidates;

        // The client's literal requested model (not candidate 0's name - see
        // ModelRouteResolutionResult.RequestedModelName), reported as RequestedModel in telemetry even
        // when a substitution or fallback actually served the request - so a dashboard shows "asked for
        // X, served by Y".
        var requestedModelName = resolution.RequestedModelName!;

        // Budget enforcement (Governance > Providers): a provider whose monthly cap is exhausted is skipped
        // for this request (the in-loop check below), so an under-budget fallback serves it. If *every*
        // candidate provider is over budget there is nothing left to route to, so the request is rejected
        // outright with 402 - the operator asked for a hard cap. This is deliberately distinct from the
        // outage failover cascade below: a breached provider is never attempted at all, whereas failover
        // reacts to a provider that *was* attempted and failed at the transport layer.
        if (_budgetStore is not null && candidates.All(c => _budgetStore.IsBreached(c.Route.Provider)))
        {
            _logger.LogWarning(
                "All candidate providers for model {Model} are over their monthly budget; rejecting with 402.",
                SanitizeForLog(requestedModelName));
            await WriteBudgetExhaustedResponseAsync(context, requestedModelName);
            return;
        }

        // Try the requested model first, then each configured fallback in order. A candidate is
        // retried against the next one only on a genuine upstream
        // *outage* - connection failure, timeout, or 5xx - never on a client-fault status (400/401/403/
        // 422), which a backup would reject identically. 429 is a special case: retried only when the next
        // backup is a *different* provider (a separate quota/rate-limit pool), since a same-provider backup
        // shares the throttle. Failover can only happen before any response byte is committed to the
        // client; once a hop's body starts streaming, its outcome is final.
        for (var i = 0; i < candidates.Count; i++)
        {
            var route = candidates[i].Route;

            // Skip a candidate whose provider is over its monthly budget - it is never attempted. The
            // pre-loop check above guarantees at least one candidate is under budget, so this cannot skip
            // every iteration. A budget-skipped primary means the served candidate is a genuine fallback,
            // which the isFallback flag (i > 0) then reports truthfully.
            if (_budgetStore is not null && _budgetStore.IsBreached(route.Provider))
            {
                _logger.LogInformation(
                    "Skipping provider {Provider} for model {Model}: monthly budget exhausted.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                continue;
            }

            // Governance > Providers' Stop control: a disabled provider is bypassed with no network call,
            // exactly like an open circuit, but logged distinctly so an operator can tell "I stopped this"
            // from "this is unhealthy" apart. Checked here (not just when RequestInterceptor built the
            // candidate list) so a provider stopped between list-building and this attempt is still caught.
            if (!_interceptor.IsProviderEnabled(route.Provider))
            {
                _logger.LogInformation(
                    "Bypassing provider {Provider} for model {Model}: provider is stopped.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                continue;
            }

            // The model-level twin of the provider check above: a model stopped via its own Start/Stop
            // toggle, or dropped by the last "Refresh from endpoint" scan (not currently reported by the
            // provider's endpoint), is bypassed the same way - logged distinctly so "I stopped this model"
            // and "the endpoint doesn't currently serve this" are each identifiable, same rationale as the
            // provider-vs-circuit distinction above.
            if (!_interceptor.IsModelEnabled(route.ModelName))
            {
                _logger.LogInformation(
                    "Bypassing model {Model}: stopped or not currently reported by its provider's endpoint.",
                    SanitizeForLog(route.ModelName));
                continue;
            }

            var circuitTarget = CircuitBreakerTargetKey.FromRoute(route);

            // Read-only pre-check for both gates before either mutating one is touched. ShouldBypass and
            // ShouldBypassProvider (below) can each transition their target from OPEN to HALF-OPEN and claim
            // its single probe slot - if that claim is made and *then* the other gate turns out to reject
            // this candidate anyway, the claimed probe would never be resolved via RecordSuccess/RecordFailure
            // (this candidate is never attempted) and would stay "in flight" forever, permanently bypassing
            // that target. Filtering out the deterministic, non-racing case here first (an already-OPEN
            // circuit still cooling down) means neither mutating check below is even reached for it.
            if (_circuitBreaker.IsOpen(circuitTarget) || _circuitBreaker.IsProviderOpen(route.Provider))
            {
                _logger.LogInformation(
                    "Bypassing provider {Provider} for model {Model}: circuit breaker is open.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                continue;
            }

            // docs/router/agent-resilience-strategies.md's Circuit Breaker: an OPEN target is bypassed with
            // no network call at all. Checked here (not just when RequestInterceptor built the candidate
            // list) so a target that trips between list-building and this attempt - or whose half-open
            // probe slot another concurrent request just claimed - is still caught, and so that this exact
            // moment is what claims the single half-open probe (see ICircuitBreaker.ShouldBypass).
            if (_circuitBreaker.ShouldBypass(circuitTarget))
            {
                _logger.LogInformation(
                    "Bypassing provider {Provider} for model {Model}: circuit breaker is open.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                continue;
            }

            // A 401 (see below) trips every model on a provider at once, not just the one that surfaced
            // it - checked here as a second, provider-wide gate so a *different* model on that same
            // now-untrusted provider is bypassed too, without ever having failed itself. The IsOpen/
            // IsProviderOpen pre-check above already filtered out the common case where this would reject a
            // probe ShouldBypass just claimed; what's left is the narrow race of a concurrent request
            // claiming the provider's own probe slot between the two calls.
            if (_circuitBreaker.ShouldBypassProvider(route.Provider))
            {
                _logger.LogInformation(
                    "Bypassing provider {Provider} for model {Model}: provider-wide circuit breaker is open.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                continue;
            }

            var rewrittenBody = candidates[i].RewrittenBody;
            var isFallback = i > 0;
            var hasNextCandidate = i + 1 < candidates.Count;

            // Every skip/bypass check above has passed, so this candidate is the one about to be attempted -
            // logged at Debug (not Information, unlike the skip/bypass messages above) since it fires on
            // every single attempt, including the common case of a primary succeeding on the first try.
            _logger.LogDebug(
                "Attempting candidate {Index}/{Total}: provider={Provider} model={Model} providerModelId={ProviderModelId} baseUrl={BaseUrl} isFallback={IsFallback}",
                i + 1,
                candidates.Count,
                SanitizeForLog(route.Provider),
                SanitizeForLog(route.ModelName),
                SanitizeForLog(route.ProviderModelId),
                route.UpstreamBaseUrl,
                isFallback);

            // Whether *any* remaining candidate is on a different provider tells us whether a 429/401 is
            // worth failing over toward at all (a separate quota pool / credential). Checked across every
            // remaining candidate, not just the immediate next one: since every other configured model is
            // now automatically a candidate (not a hand-curated short chain), a same-provider model can sit
            // between this failing one and a genuinely distinct-provider backup - a same-provider candidate
            // in between gets skipped harmlessly by the provider-wide circuit-breaker bypass check above
            // once this failure trips it (see RecordProviderFailure), so it's never actually attempted.
            var nextProviderDiffers = hasNextCandidate &&
                candidates.Skip(i + 1).Any(c => !string.Equals(c.Route.Provider, route.Provider, StringComparison.OrdinalIgnoreCase));

            // A provider whose native API shape differs from OpenAI's has an IPayloadTranslator registered
            // (Gemini, Anthropic, and the Bedrock providers today); every other provider has none and keeps
            // the byte-for-byte pass-through path below unchanged.
            _translators.TryGetValue(route.Provider, out var translator);

            // A provider with no registered translator can still need its *response* normalized, when the
            // model it serves expresses tool calls as text rather than as an OpenAI tool_calls delta
            // (docs/router/tool-call-normalization.md). Unlike every translator above this is selected per
            // (provider, model) and per request rather than by provider key, because a model's tool-call
            // syntax comes from its chat template: one local server serves both a model that needs
            // rewriting and one that must never be scanned. Returning null - the common case - keeps the
            // byte-for-byte pass-through path below exactly as it was.
            translator ??= _toolCallNormalizerFactory.TryCreate(
                route,
                candidates[i].CarriesTools,
                candidates[i].CarriesToolHistory,
                candidates[i].CarriesResponseFormat);

            // Bedrock providers are invoked through the AWS SDK (SigV4 signing, endpoint resolution, and -
            // for streaming - AWS's binary event-stream decoding are all the SDK's job, not a forwarded
            // HttpRequestMessage's), which is a different enough invocation shape that it gets its own path
            // entirely rather than reusing the HttpClient-forwarding code below. A Bedrock candidate is no
            // longer unconditionally terminal: InvokeBedrockAsync returns true once it has actually written
            // a response to the client (success, or a failure with no eligible next candidate) - that's
            // this request's final outcome, so the loop stops. It returns false when the SDK call failed
            // *before* writing anything (same "nothing committed yet" invariant the HTTP path below relies
            // on) and a next candidate is worth trying, in which case the loop falls through to `continue`
            // exactly like an HTTP outage does.
            if (translator is IBedrockPayloadTranslator bedrockTranslator)
            {
                if (await InvokeBedrockAsync(context, route, bedrockTranslator, rewrittenBody, requestedModelName, isFallback, hasNextCandidate, nextProviderDiffers, resolution.TaskEmbedding, resolution.RouterTokens, resolution.SubstitutionReason, resolution.IsExploratory, resolution.Propensity, resolution.Classification, resolution.TaskText, resolution.DimBestModel))
                {
                    return;
                }

                continue;
            }

            // When a translator exists, it - not the request path - decides the upstream URL (Gemini
            // encodes the model id + streaming choice in the path) and rewrites the body. Anthropic is
            // dual-mode: the same provider key also carries real Claude Code traffic that is already
            // Anthropic-native, so ShouldTranslate can veto translation for this specific request (by path)
            // even though a translator is registered for the provider.
            if (translator is not null && !translator.ShouldTranslate(context.Request))
            {
                translator = null;
            }

            // A response-only translator is treated like "no translator" for request-forwarding purposes,
            // while still being "not null" below for response handling (streaming/buffered dispatch,
            // Content-Length/Content-Encoding stripping, etc.) - see IResponseOnlyTranslator for why its
            // request side cannot be routed through BuildRequestUri.
            var isRequestReshapingTranslator = translator is not null and not IResponseOnlyTranslator;

            // Reshaping the body and owning the upstream URL are two axes, not one. Every translator before
            // Phase 5 did both or neither, so one flag covered it; tool-call emulation rewrites the body
            // heavily while still addressing the same OpenAI-compatible endpoint on the client's own path -
            // see IClientPathTranslator.
            var buildsOwnRequestUri = isRequestReshapingTranslator && translator is not IClientPathTranslator;

            var requestIsStreaming = buildsOwnRequestUri && IsStreamingRequest(rewrittenBody);

            Uri targetUri;
            byte[] forwardBody;
            if (buildsOwnRequestUri)
            {
                targetUri = translator!.BuildRequestUri(route.UpstreamBaseUrl, route.ProviderModelId, requestIsStreaming);
                forwardBody = translator.TranslateRequest(rewrittenBody);
            }
            else
            {
                // Deliberately neither `new Uri(route.UpstreamBaseUrl, relativePath)` nor plain string
                // concatenation - both are lossy in opposite directions, and BuildPassthroughUrl exists to
                // be neither. Combining drops a BaseUrl's own path (an ASP.NET Core path always starts with
                // "/", making it an absolute-path reference that *replaces* the base's path); concatenating
                // emits a shared prefix twice, so an LM Studio base of "http://127.0.0.1:1234/v1" meeting a
                // client's "/v1/chat/completions" forwards to "/v1/v1/chat/completions" - which LM Studio
                // answers 200 with an error body, so it fails silently everywhere downstream. See
                // ProviderUrlBuilder.BuildPassthroughUrl and src/README.md's "Provider base URLs".
                // `.Value` on both rather than PathString's implicit string conversion and
                // QueryString.ToString(). The two disagree about empty - `Value` is null, ToString() is ""
                // - and BuildPassthroughUrl accepts either, so the choice is about which one says so. The
                // explicit nullable spelling is the one that matches the documented contract; going
                // through ToString() would quietly guarantee non-null here and make the null handling on
                // the other side look like dead defensive code the next reader deletes.
                targetUri = new Uri(ProviderUrlBuilder.BuildPassthroughUrl(
                    route.UpstreamBaseUrl,
                    context.Request.Path.Value,
                    context.Request.QueryString.Value));

                // Still consulted for the body, so a client-path translator gets its rewrite. A
                // response-only translator's TranslateRequest is the identity by contract, which keeps this
                // the byte-for-byte forwarding path it has always been for everything else.
                forwardBody = isRequestReshapingTranslator ? translator!.TranslateRequest(rewrittenBody) : rewrittenBody;
            }

            var requestMessage = new HttpRequestMessage
            {
                RequestUri = targetUri,
                Method = new HttpMethod(context.Request.Method)
            };

            var requestHopByHopHeaders = GetHopByHopHeaderNames(
                context.Request.Headers.TryGetValue("Connection", out var requestConnectionValues) ? requestConnectionValues : default);

            // A client header matching the provider's configured auth header name is only skipped here when
            // the provider's configuration actually declares one (route.AuthHeaderConfigured) - otherwise an
            // unauthenticated provider (e.g. a free local runtime with no auth header entry) would silently
            // drop a client's own header of that name with nothing forwarded in its place. This is deliberately
            // based on configuration intent rather than whether the header resolved into route.ExtraHeaders
            // *this request* - a provider whose credential env var is temporarily unset must still have the
            // client's own header stripped (failing closed with no credential forwarded), not let the client's
            // header through as a stand-in for the operator-configured one.
            var providerSuppliesAuthHeader = route.AuthHeaderConfigured;

            foreach (var header in context.Request.Headers)
            {
                if (AlwaysSkippedRequestHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase) ||
                    requestHopByHopHeaders.Contains(header.Key) ||
                    (providerSuppliesAuthHeader && string.Equals(header.Key, route.AuthHeaderName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            requestMessage.Content = new ByteArrayContent(forwardBody);
            requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

            // Provider-configured custom headers (e.g. anthropic-version, and whichever header carries
            // authentication). Added only when the client didn't already send that header, so a client
            // supplying its own value keeps it rather than having it clobbered or duplicated. Client headers
            // were copied into requestMessage.Headers above - except the auth header, which was skipped there
            // by name (and thus sourced from here instead) only when the provider actually has one configured
            // (route.AuthHeaderConfigured); for a provider with no auth header configured, the client's own
            // header of that name was left in place and nothing here touches it.
            foreach (var (headerName, headerValue) in route.ExtraHeaders)
            {
                if (!requestMessage.Headers.Contains(headerName))
                {
                    requestMessage.Headers.TryAddWithoutValidation(headerName, headerValue);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage responseMessage;
            try
            {
                responseMessage = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (Exception ex) when (IsTransportOutage(ex, context.RequestAborted))
            {
                // Connection refused/reset, DNS failure, or the HttpClient timeout (not a client abort):
                // the upstream is effectively down. Fail over to the next backup if one remains; otherwise
                // this is the end of the cascade and the client sees a 502.
                _circuitBreaker.RecordFailure(circuitTarget);
                requestMessage.Dispose();
                if (hasNextCandidate)
                {
                    _logger.LogWarning(ex, "Upstream provider {Provider} unreachable for model {Model}; failing over to the next backup.", SanitizeForLog(route.Provider), SanitizeForLog(route.ModelName));
                    continue;
                }

                _logger.LogWarning(ex, "Upstream provider {Provider} unreachable for model {Model}; no backup remains.", SanitizeForLog(route.Provider), SanitizeForLog(route.ModelName));
                if (!context.Response.HasStarted)
                {
                    // Deliberately a generic message, not ex.Message: transport-exception text can carry
                    // internal hostnames, DNS/socket details, or configured base URLs. The full exception
                    // is already logged above for operators; the client only needs "upstream unavailable."
                    await WriteUpstreamErrorResponseAsync(context, "The upstream provider is unavailable.");
                }

                return;
            }

            var latencyToHeadersMs = stopwatch.ElapsedMilliseconds;
            var statusCode = (int)responseMessage.StatusCode;

            // Headers precede the body for both streaming and buffered responses, so capture happens here
            // rather than after the usage-parsing pass below (which needs the fully captured body first).
            // Best-effort and self-guarding (see IRateLimitHeaderCapture's contract): the call only enqueues
            // onto the capture's own background consumer and returns immediately, and is deliberately not
            // awaited here so the SQLite write it eventually does can never delay emitting the response
            // already back from upstream. Errors are caught and logged inside the capture implementation
            // itself, so there is nothing for an unobserved-task-exception handler to catch.
            _ = _rateLimitCapture.CaptureAsync(route.Provider, responseMessage.Headers, CancellationToken.None);

            // Gemini reports an invalid/expired API key as a 400 (the key travels as a "key=" query
            // parameter, not an Authorization header, so Google's gateway treats it as a malformed
            // request rather than a 401) with an embedded {"status": "UNAUTHENTICATED"} error - a plain
            // 400/401 status-code check alone can't tell that apart from a genuinely malformed request.
            // The body is buffered here (small - it's an error payload, never the multi-chunk token
            // stream a real completion produces) so it can be inspected before anything is written to
            // the client; whatever this finds also replaces the translator's success-shaped
            // TranslateResponse/stream translation below, which would otherwise mangle or (for the SSE
            // shape) silently swallow the error text instead of surfacing it.
            byte[]? preReadErrorBody = null;
            string? geminiEmbeddedErrorMessage = null;
            var isGeminiAuthFailure = false;
            if (statusCode == StatusCodes.Status400BadRequest && translator is GeminiPayloadTranslator)
            {
                preReadErrorBody = await responseMessage.Content.ReadAsByteArrayAsync(context.RequestAborted);
                if (GeminiPayloadTranslator.TryExtractEmbeddedError(preReadErrorBody, out var embeddedStatus, out var embeddedMessage))
                {
                    geminiEmbeddedErrorMessage = embeddedMessage;
                    isGeminiAuthFailure = string.Equals(embeddedStatus, "UNAUTHENTICATED", StringComparison.Ordinal) ||
                        embeddedMessage.Contains("API key not valid", StringComparison.OrdinalIgnoreCase);
                }
            }

            // Circuit-breaker health signal. A 401, 403, or 405 is treated as a *provider-wide* outage: a 401
            // is almost always an invalid/expired credential, a 403 is almost always a permission/API-key-scope
            // problem (e.g. "API not enabled for this key", region lock, tier restriction) rather than
            // something specific to the one model requested, and a 405 on a path this proxy itself constructs
            // (the client never chooses the method) is almost always a provider-side gateway/WAF rejecting
            // the request at the edge - all three would identically break every model on this provider, not
            // just the one the client happened to ask for, so all three trip every model on the provider at
            // once (RecordProviderFailure) rather than just this target (RecordFailure). (This is distinct
            // from a *client*-authored 405 against ArcRouter's own listener, e.g. a GET to
            // /v1/chat/completions, which never reaches this upstream-handling code at all - see
            // IsModelsListRequest/routing earlier in the pipeline.) Any 5xx, 429, or 404 still counts as a
            // per-target failure regardless of whether a backup is actually attempted next (that's
            // IsRetriableOutageStatus's separate concern, below) - a same-provider 429 that isn't worth
            // failing over to a backup for is still proof this target itself is unhealthy right now, and a
            // 404 is proof this target's configured model id is wrong/gone (unlike 401/403/405, that's a
            // per-target misconfiguration, not a provider-wide problem, so it stays per-target rather than
            // tripping RecordProviderFailure). Everything else - 2xx, and client-fault 4xx like 400/422 -
            // means the target answered as configured, so it counts as a success (which also clears this
            // target's provider-wide state - see ICircuitBreaker.RecordSuccess).
            if (statusCode == StatusCodes.Status401Unauthorized)
            {
                _logger.LogError(
                    "Upstream provider {Provider} returned 401 Unauthorized for model {Model}; treating as a provider-wide outage (likely an invalid or expired credential) and bypassing every model on this provider until it recovers.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                _circuitBreaker.RecordProviderFailure(route.Provider);
            }
            else if (isGeminiAuthFailure)
            {
                _logger.LogError(
                    "Upstream provider {Provider} returned 400 with an embedded UNAUTHENTICATED error for model {Model} (Gemini reports an invalid API key as 400, not 401); treating as a provider-wide outage and bypassing every model on this provider until it recovers.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                _circuitBreaker.RecordProviderFailure(route.Provider);
            }
            else if (statusCode == StatusCodes.Status403Forbidden)
            {
                _logger.LogError(
                    "Upstream provider {Provider} returned 403 Forbidden for model {Model}; treating as a provider-wide outage (likely a permission or API-key-scope problem) and bypassing every model on this provider until it recovers.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                _circuitBreaker.RecordProviderFailure(route.Provider);
            }
            else if (statusCode == StatusCodes.Status405MethodNotAllowed)
            {
                // Seen in production as an Alibaba Cloud WAF block page (an HTML "access blocked" response,
                // not a real API error) served instead of reaching the model API at all - a gateway-level
                // rejection of this request, not evidence about the request's actual validity.
                _logger.LogError(
                    "Upstream provider {Provider} returned 405 Method Not Allowed for model {Model}; treating as a provider-wide gateway/WAF block and bypassing every model on this provider until it recovers.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                _circuitBreaker.RecordProviderFailure(route.Provider);
            }
            else if (IsOutageStatus(statusCode))
            {
                _circuitBreaker.RecordFailure(circuitTarget);
            }
            else
            {
                _circuitBreaker.RecordSuccess(circuitTarget);
            }

            // A retriable *outage* status: a 5xx (provider-side failure), a 404 (this target's configured
            // model doesn't exist/is gone - says nothing about a *different*, already-configured candidate,
            // so it's retried unconditionally, unlike 401/403/405/429 below), or a 401/403 (provider-wide
            // credential or permission failure)/405 (provider-wide gateway/WAF block - see the
            // circuit-breaker comment above)/429 (rate limit) - the latter four only when the next backup is
            // a *separate* provider, since a same-provider backup shares the same broken credential,
            // permission scope, gateway policy, or throttle. Other client-fault statuses (400/422) are never
            // retried - a backup would reject the same request identically. Only fail over while a backup
            // remains and nothing has been written to the client yet.
            //
            // isGeminiAuthFailure is folded in here alongside the numeric-status check: it's Gemini's 400
            // disguising what is, semantically, a 401 (see where it's computed above), so it gets the same
            // different-provider-only retry treatment as the real 401/403/405 case.
            if (hasNextCandidate && (IsRetriableOutageStatus(statusCode, nextProviderDiffers) || (isGeminiAuthFailure && nextProviderDiffers)))
            {
                _logger.LogWarning("Upstream provider {Provider} returned {Status} for model {Model}; failing over to the next backup.", SanitizeForLog(route.Provider), statusCode, SanitizeForLog(route.ModelName));
                responseMessage.Dispose();
                requestMessage.Dispose();
                continue;
            }

            // This hop is the one that answers the client: a success, a non-retriable status, or the last
            // candidate. Commit its response and stop.
            using (responseMessage)
            using (requestMessage)
            {
                var responseHopByHopHeaders = GetHopByHopHeaderNames(responseMessage.Headers.Connection);

                context.Response.StatusCode = statusCode;
                foreach (var header in responseMessage.Headers)
                {
                    if (responseHopByHopHeaders.Contains(header.Key))
                    {
                        continue;
                    }

                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                foreach (var header in responseMessage.Content.Headers)
                {
                    if (responseHopByHopHeaders.Contains(header.Key))
                    {
                        continue;
                    }

                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                var isStreaming = string.Equals(responseMessage.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

                // A translated body no longer matches the upstream's own Content-Length (or, for streaming, its
                // Content-Type framing is re-emitted by us): drop the copied Content-Length so Kestrel sizes the
                // rewritten body itself rather than truncating it against a stale length. Content-Encoding is
                // dropped for the same reason and belt-and-suspenders alongside skipping the client's own
                // Accept-Encoding on the way upstream (see AlwaysSkippedRequestHeaders): the translator always
                // writes fresh, uncompressed UTF-8 text, so a copied "Content-Encoding: gzip" (or any other
                // value) would be a lie the client then fails to decode.
                if (translator is not null)
                {
                    context.Response.Headers.Remove("Content-Length");
                    context.Response.Headers.Remove("Content-Encoding");
                }

                // docs/router/orchestrator-live-path-plan.md §M2.2: requested-vs-routed surfaced in
                // response headers (not the provider-shaped JSON body) so it works identically for
                // streaming and buffered responses. Set before any body byte is written, alongside the
                // rest of this hop's response headers above.
                context.Response.Headers[RequestedModelHeaderName] = requestedModelName;
                context.Response.Headers[RoutedModelHeaderName] = route.ModelName;
                context.Response.Headers[SubstitutionReasonHeaderName] = ResolveSubstitutionReason(isFallback, resolution.SubstitutionReason).ToString();

                byte[] capturedResponseBytes;
                byte[]? nativeResponseBytes = null;
                IncrementalUsageScanner? tailScanner = null;
                if (preReadErrorBody is not null && geminiEmbeddedErrorMessage is not null)
                {
                    // A Gemini 400 that reached here (either it wasn't the UNAUTHENTICATED case, or it was
                    // but no differing-provider candidate remained to fail over to) whose body actually
                    // contained an embedded error object (TryExtractEmbeddedError succeeded). The body was
                    // already read above to make that determination; running it through TranslateResponse/the
                    // stream translator now would mangle it into a bogus empty completion or (for the SSE
                    // shape) silently swallow it - see TryExtractEmbeddedError's caller - so a clean
                    // OpenAI-shaped error is written directly instead, preserving whatever message Gemini
                    // actually sent.
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.Remove("Content-Length");
                    var errorPayload = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        error = new
                        {
                            message = geminiEmbeddedErrorMessage,
                            type = "invalid_request_error",
                            param = (string?)null,
                            code = statusCode.ToString(),
                        },
                    });
                    await context.Response.Body.WriteAsync(errorPayload, context.RequestAborted);
                    capturedResponseBytes = errorPayload;
                }
                else if (preReadErrorBody is not null)
                {
                    // A Gemini 400 whose body didn't contain a recognizable embedded error object
                    // (TryExtractEmbeddedError returned false) - per its contract, forward the raw body
                    // unchanged rather than losing it behind a synthetic generic message.
                    context.Response.Headers.Remove("Content-Length");
                    await context.Response.Body.WriteAsync(preReadErrorBody, context.RequestAborted);
                    capturedResponseBytes = preReadErrorBody;
                }
                else
                {
                    using var upstreamBody = await responseMessage.Content.ReadAsStreamAsync(context.RequestAborted);
                    try
                    {
                        if (translator is null)
                        {
                            (capturedResponseBytes, tailScanner) = await CopyAndCaptureAsync(upstreamBody, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted);
                        }
                        else
                        {
                            var captured = isStreaming
                                ? await TranslateAndCaptureStreamAsync(translator, upstreamBody, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted)
                                : await TranslateAndCaptureBufferedAsync(translator, upstreamBody, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted);
                            capturedResponseBytes = captured.ClientShapeBytes;
                            nativeResponseBytes = captured.NativeBytes;
                            tailScanner = captured.TailScanner;
                        }
                    }
                    catch (Exception ex) when (!context.Response.HasStarted && IsStreamAbort(ex))
                    {
                        // Only TranslateAndCaptureBufferedAsync can reach here: it's the one dispatch path
                        // above that can fail before writing anything (it buffers the whole upstream body
                        // before the one translated write), and it only rethrows a non-client-abort I/O
                        // failure (see its own catch). CopyAndCaptureAsync/TranslateAndCaptureStreamAsync
                        // always fail open internally instead of throwing, since they write incrementally
                        // and so have necessarily already started the response by the time anything could
                        // go wrong. Nothing has been committed yet, so this can still become a clean 502
                        // instead of silently returning a 200 with an empty body.
                        _logger.LogWarning(ex, "Buffered upstream read failed before any response bytes were sent to the client; reporting an upstream error instead of an empty success.");
                        await WriteUpstreamErrorResponseAsync(context, "The upstream provider closed the connection unexpectedly.");
                        return;
                    }
                }

                var totalDurationMs = stopwatch.ElapsedMilliseconds;

                await _interceptor.InterceptResponseAsync(context);

                // Telemetry is best-effort observability layered on top of an already-completed forward: every
                // byte of the response has already reached the client by this point, and any failure here
                // (malformed JSON, an extractor throwing, a disconnected dashboard) must never surface as a
                // proxy error.
                try
                {
                    // The bytes telemetry parses are whatever actually reached the client: OpenAI-shaped when a
                    // translator ran this request (Gemini always; Anthropic only when ShouldTranslate allowed
                    // it), the provider's own native shape otherwise (openai/ollama pass-through, or Anthropic's
                    // native /v1/messages traffic that ShouldTranslate vetoed above). Passing "openai" for a
                    // translated response - rather than route.Provider - is what lets UsageExtractor/
                    // ResponseTextExtractor pick the right parser per request instead of assuming one shape per
                    // provider, which broke once "anthropic" became dual-mode.
                    var telemetryShapeProvider = translator is not null ? "openai" : route.Provider;
                    await PublishTelemetryAsync(context, route, requestedModelName, isFallback, telemetryShapeProvider, rewrittenBody, capturedResponseBytes, nativeResponseBytes, isStreaming, latencyToHeadersMs, totalDurationMs, statusCode, context.RequestAborted, responseMessage.Headers, tailScanner, resolution.TaskEmbedding, resolution.RouterTokens, resolution.SubstitutionReason, resolution.IsExploratory, resolution.Propensity, resolution.Classification, resolution.TaskText, resolution.DimBestModel);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to publish routing telemetry; the forwarded response was unaffected.");
                }
            }

            return;
        }

        // Falling out of the loop means every candidate was ultimately skipped, never attempted: either a
        // concurrent budget edit (via the management API) breached the last under-budget candidate after
        // the pre-loop all-breached check passed, every candidate's provider was stopped via Governance >
        // Providers, every candidate's model was stopped or dropped by its provider's last endpoint scan,
        // or every candidate's circuit breaker is currently OPEN (all configured routes for this model are
        // presently unhealthy - docs/router/agent-resilience-strategies.md). The outage-failover `continue`s
        // only fire while a backup remains, so the only ways to exit without committing a response are a
        // budget-skip, a disabled-provider-or-model-skip, or a circuit-breaker-skip on the final candidate.
        if (!context.Response.HasStarted)
        {
            if (_budgetStore is not null && candidates.All(c => _budgetStore.IsBreached(c.Route.Provider)))
            {
                _logger.LogWarning(
                    "All candidate providers for model {Model} became over budget mid-request; rejecting with 402.",
                    SanitizeForLog(requestedModelName));
                await WriteBudgetExhaustedResponseAsync(context, requestedModelName);
            }
            else if (candidates.All(c => !_interceptor.IsProviderEnabled(c.Route.Provider) || !_interceptor.IsModelEnabled(c.Route.ModelName)))
            {
                _logger.LogWarning(
                    "All candidate routes for model {Model} are stopped (provider stopped, model stopped, or not currently reported by its provider's endpoint); rejecting with 503.",
                    SanitizeForLog(requestedModelName));
                await WriteUpstreamErrorResponseAsync(
                    context,
                    "All configured routes for this model are currently stopped.",
                    StatusCodes.Status503ServiceUnavailable);
            }
            else
            {
                _logger.LogWarning(
                    "All candidate routes for model {Model} are currently circuit-broken; rejecting with 502.",
                    SanitizeForLog(requestedModelName));
                await WriteUpstreamErrorResponseAsync(context, "All configured routes for this model are currently unavailable.");
            }
        }
    }

    /// <summary>
    /// Classifies an exception thrown by <see cref="HttpClient.SendAsync(HttpRequestMessage)"/> as a
    /// transport-level upstream outage worth failing over from: a connection failure
    /// (<see cref="HttpRequestException"/>) or the client's own send timeout (an
    /// <see cref="OperationCanceledException"/> that is <em>not</em> the inbound client aborting the
    /// request). A genuine client abort (<paramref name="requestAborted"/> already cancelled) is not an
    /// outage - the client went away, so there is nothing to fail over to - and is left to propagate.
    /// </summary>
    private static bool IsTransportOutage(Exception ex, CancellationToken requestAborted) => ex switch
    {
        HttpRequestException => true,
        OperationCanceledException => !requestAborted.IsCancellationRequested,
        _ => false,
    };

    /// <summary>
    /// Classifies an exception raised while streaming an already-200-OK response body to the client as a
    /// mid-stream abort worth failing open from, rather than letting it crash the request pipeline: the
    /// client disconnected, the underlying connection was reset, or the process itself is tearing down
    /// (e.g. a <c>dotnet watch</c> hot reload or debugger stop killing the socket out from under an
    /// in-flight upstream read - <see cref="IOException"/> wrapping a <see cref="SocketException"/> with
    /// "aborted because of either a thread exit or an application request" is that exact signature).
    /// Headers and any earlier chunks have already reached the client at this point, so - like the
    /// existing <see cref="GeminiStreamException"/>/<see cref="AnthropicStreamException"/> handling - there
    /// is nothing to do but stop forwarding and log it; the status can no longer change.
    /// </summary>
    private static bool IsStreamAbort(Exception ex) => ex is OperationCanceledException or IOException or SocketException;

    /// <summary>
    /// Determines whether an upstream HTTP <paramref name="statusCode"/> is a retriable outage: any 5xx
    /// (a provider-side failure), a 404 (this target's configured model doesn't exist/is gone - a
    /// per-candidate fact that says nothing about a different, already-configured candidate, so it is
    /// retried unconditionally, regardless of <paramref name="nextBackupIsDifferentProvider"/>), or a 429
    /// (rate limit / out of credits), 401 (invalid/expired credential), 403 (permission/API-key-scope
    /// problem - e.g. API not enabled for this key, region lock, tier restriction), or 405 (provider-side
    /// gateway/WAF block, e.g. an edge security page rejecting the request before it reaches the model API -
    /// provider-wide, see <see cref="ICircuitBreaker.RecordProviderFailure"/>) when
    /// <paramref name="nextBackupIsDifferentProvider"/> is <see langword="true"/>. A 429/401/403/405 whose
    /// only remaining backup shares the same provider is <em>not</em> retried, because a shared quota pool,
    /// credential, permission scope, or gateway/WAF policy would fail the backup identically - retrying it
    /// would only delay surfacing the failure to the client. That same-fate reasoning does not apply to a
    /// 404, which is specific to this one candidate. All other statuses - including client-fault 4xx such as
    /// 400/422 - are never retried.
    /// </summary>
    private static bool IsRetriableOutageStatus(int statusCode, bool nextBackupIsDifferentProvider)
    {
        if (statusCode is >= 500 and <= 599)
        {
            return true;
        }

        if (statusCode == StatusCodes.Status404NotFound)
        {
            return true;
        }

        return (statusCode == 429
            || statusCode == StatusCodes.Status401Unauthorized
            || statusCode == StatusCodes.Status403Forbidden
            || statusCode == StatusCodes.Status405MethodNotAllowed)
            && nextBackupIsDifferentProvider;
    }

    /// <summary>
    /// Determines whether an upstream HTTP <paramref name="statusCode"/> counts as a per-target
    /// circuit-breaker failure: any 5xx, a 429 (rate limit / out of credits), or a 404 (this target's
    /// configured model doesn't exist/is gone - a per-target misconfiguration, unlike 401/403/405 which are
    /// handled separately as provider-wide - see the caller). Unlike <see cref="IsRetriableOutageStatus"/>,
    /// this does not depend on whether a backup exists or shares the same provider - each of these statuses
    /// is proof <em>this</em> target is unhealthy right now regardless of what (if anything) the request
    /// fails over to next.
    /// </summary>
    private static bool IsOutageStatus(int statusCode) =>
        statusCode is >= 500 and <= 599 || statusCode == 429 || statusCode == StatusCodes.Status404NotFound;

    /// <summary>
    /// Invokes an Amazon Bedrock provider via the AWS SDK (<see cref="IAmazonBedrockRuntime"/>) instead
    /// of the raw-HTTP forwarding path every other translated provider uses - the SDK handles SigV4
    /// signing, endpoint resolution, and (for streaming) AWS's binary event-stream decoding internally,
    /// so none of that is this method's concern. Bedrock is always translated (no native-passthrough
    /// mode exists for it, unlike Anthropic), so the response is always OpenAI-shaped by the time
    /// telemetry parses it.
    /// </summary>
    /// <param name="context">The inbound request/response context.</param>
    /// <param name="route">The resolved candidate to attempt.</param>
    /// <param name="translator">The Bedrock payload translator for this candidate's provider.</param>
    /// <param name="rewrittenBody">The request body with <c>model</c> already rewritten to this candidate.</param>
    /// <param name="requestedModelName">The client's originally requested model name (for telemetry).</param>
    /// <param name="isFallback">Whether this candidate is a fallback (not the primary) (for telemetry).</param>
    /// <param name="hasNextCandidate">Whether another candidate remains in the caller's cascade.</param>
    /// <param name="nextProviderDiffers">
    /// Whether any remaining candidate is on a different provider - mirrors the HTTP path's identically
    /// named local, used the same way: a credential failure is only worth retrying against a genuinely
    /// different provider, since a same-provider backup shares the identical broken credential.
    /// </param>
    /// <param name="taskEmbedding">The request's task embedding (see <see cref="ModelRouteResolutionResult.TaskEmbedding"/>), forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <param name="routerTokens">The router's own token consumption for this request (see <see cref="ModelRouteResolutionResult.RouterTokens"/>), forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <param name="resolutionReason">The resolution-time <see cref="RoutingSubstitutionReason"/> (see <see cref="ModelRouteResolutionResult.SubstitutionReason"/>), forwarded to <see cref="PublishTelemetryAsync"/> and the requested/routed response headers.</param>
    /// <param name="isExploratory">See <see cref="ModelRouteResolutionResult.IsExploratory"/>, forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <param name="propensity">See <see cref="ModelRouteResolutionResult.Propensity"/>, forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <param name="classification">See <see cref="ModelRouteResolutionResult.Classification"/>, forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <param name="taskText">See <see cref="ModelRouteResolutionResult.TaskText"/>, forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <param name="dimBestModel">See <see cref="ModelRouteResolutionResult.DimBestModel"/>, forwarded to <see cref="PublishTelemetryAsync"/>.</param>
    /// <returns>
    /// <see langword="true"/> if a response was written to the client (success, or a failure with no
    /// eligible next candidate) - the caller's cascade is over. <see langword="false"/> if the SDK call
    /// failed before anything was written and a next candidate should be tried - the caller should
    /// <c>continue</c> its loop.
    /// </returns>
    private async Task<bool> InvokeBedrockAsync(
        HttpContext context,
        ResolvedModelRoute route,
        IBedrockPayloadTranslator translator,
        byte[] rewrittenBody,
        string requestedModelName,
        bool isFallback,
        bool hasNextCandidate,
        bool nextProviderDiffers,
        float[]? taskEmbedding,
        int routerTokens,
        RoutingSubstitutionReason resolutionReason,
        bool isExploratory = false,
        double propensity = 1.0,
        RequestClassification? classification = null,
        string? taskText = null,
        string? dimBestModel = null)
    {
        var circuitTarget = CircuitBreakerTargetKey.FromRoute(route);
        var nativeRequestBody = translator.TranslateRequest(rewrittenBody);
        var isStreamingRequest = IsStreamingRequest(rewrittenBody);

        // Not disposed here: the singleton factory owns the client's lifetime and reuses it across
        // requests (AWS SDK clients are thread-safe and meant to be long-lived). See BedrockRuntimeClientFactory.
        var client = _bedrockClientFactory.Create(route);

        var stopwatch = Stopwatch.StartNew();
        byte[] capturedResponseBytes;
        long latencyToHeadersMs;
        IncrementalUsageScanner? tailScanner = null;

        try
        {
            if (isStreamingRequest)
            {
                var request = new InvokeModelWithResponseStreamRequest
                {
                    ModelId = route.ProviderModelId,
                    Body = new MemoryStream(nativeRequestBody),
                    ContentType = "application/json",
                };

                var response = await client.InvokeModelWithResponseStreamAsync(request, context.RequestAborted);
                latencyToHeadersMs = stopwatch.ElapsedMilliseconds;

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers[RequestedModelHeaderName] = requestedModelName;
                context.Response.Headers[RoutedModelHeaderName] = route.ModelName;
                context.Response.Headers[SubstitutionReasonHeaderName] = ResolveSubstitutionReason(isFallback, resolutionReason).ToString();

                (capturedResponseBytes, tailScanner) = await TranslateAndCaptureBedrockStreamAsync(translator, response.Body, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted);
            }
            else
            {
                var request = new InvokeModelRequest
                {
                    ModelId = route.ProviderModelId,
                    Body = new MemoryStream(nativeRequestBody),
                    ContentType = "application/json",
                };

                var response = await client.InvokeModelAsync(request, context.RequestAborted);
                latencyToHeadersMs = stopwatch.ElapsedMilliseconds;

                var translated = translator.TranslateResponse(response.Body.ToArray());

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                context.Response.Headers[RequestedModelHeaderName] = requestedModelName;
                context.Response.Headers[RoutedModelHeaderName] = route.ModelName;
                context.Response.Headers[SubstitutionReasonHeaderName] = ResolveSubstitutionReason(isFallback, resolutionReason).ToString();
                await context.Response.Body.WriteAsync(translated, context.RequestAborted);

                capturedResponseBytes = translated.Length <= MaxCapturedResponseBytes ? translated : translated[..MaxCapturedResponseBytes];

                // Only worth the ~64KB tail-window allocation when capturedResponseBytes above actually
                // lost data - a response that fits within the cap is already fully captured.
                if (translated.Length > MaxCapturedResponseBytes)
                {
                    var bufferedTailScanner = new IncrementalUsageScanner();
                    bufferedTailScanner.Append(translated);
                    tailScanner = bufferedTailScanner;
                }
            }
        }
        catch (AmazonClientException ex)
        {
            // A client-side SDK failure that never reached AWS at all - most commonly a missing/invalid
            // credential (e.g. "Failed to resolve bearer token in DefaultAWSTokenIdentityResolver" when
            // none of AwsAccessKeyIdEnvVar/AwsSecretAccessKeyEnvVar/AwsSessionTokenEnvVar resolve and the
            // SDK's default credential chain also comes up empty). AmazonServiceException (and its
            // Bedrock-specific subtype below, caught separately) is a *sibling* type - both derive directly
            // from Exception, not from AmazonClientException - so this clause and the
            // AmazonBedrockRuntimeException one below never overlap: a real service-level error (auth
            // rejected *by* AWS, throttling, etc.) only ever matches the other handler. Treated like the HTTP
            // path's 401 handling: logged at Error (not Warning - this is a provider misconfiguration an
            // operator needs to see, not a transient blip), tripping the *provider-wide* circuit
            // (RecordProviderFailure, not the per-target RecordFailure) since a missing/invalid credential
            // breaks every model on this Bedrock provider identically, and surfaced to the client as a 401
            // rather than the generic 502 other Bedrock failures get.
            _circuitBreaker.RecordProviderFailure(route.Provider);
            _logger.LogError(
                ex,
                "Bedrock invocation for provider {Provider} failed to resolve AWS credentials ({Message}); treating as an unauthorized (401) provider-wide outage and bypassing every model on this provider until it recovers.",
                SanitizeForLog(route.Provider),
                SanitizeForLog(ex.Message));

            // Same same-fate reasoning as the HTTP path's 401 handling (see the circuit-breaker comment
            // there): a same-provider backup shares the identical broken credential, so only a genuinely
            // different-provider candidate is worth retrying.
            if (hasNextCandidate && nextProviderDiffers)
            {
                _logger.LogWarning(
                    "Bedrock provider {Provider} failed to authorize for model {Model}; failing over to the next backup.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                return false;
            }

            if (!context.Response.HasStarted)
            {
                // Generic client message, not ex.Message: an AWS SDK exception can carry internal endpoint,
                // region, or request-id detail. The full exception is logged above for operators.
                await WriteUpstreamErrorResponseAsync(context, "The upstream provider rejected the request as unauthorized.", StatusCodes.Status401Unauthorized);
            }

            return true;
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            // A Bedrock SDK-level failure (auth, throttling, unknown model id, region misconfiguration,
            // etc.) surfaced before (or, for InvokeModel, without) any bytes reaching the client. Treated
            // like the HTTP path's generic 5xx/outage handling: a per-target circuit failure (RecordFailure,
            // not RecordProviderFailure - unlike the credential case above, this bucket doesn't carry a
            // confident "every model on this provider is equally broken" signal), retried against any next
            // candidate unconditionally (no same-provider exclusion - unlike a shared bad credential, a
            // throttle/misconfiguration on this one model id says nothing about a sibling model's health).
            _circuitBreaker.RecordFailure(circuitTarget);
            _logger.LogWarning(ex, "Bedrock invocation failed for provider {Provider}.", SanitizeForLog(route.Provider));

            if (hasNextCandidate)
            {
                _logger.LogWarning(
                    "Bedrock provider {Provider} failed for model {Model}; failing over to the next backup.",
                    SanitizeForLog(route.Provider),
                    SanitizeForLog(route.ModelName));
                return false;
            }

            if (!context.Response.HasStarted)
            {
                // Generic client message, not ex.Message: an AWS SDK exception can carry internal endpoint,
                // region, or request-id detail. The full exception is logged above for operators.
                await WriteUpstreamErrorResponseAsync(context, "The upstream provider is unavailable.");
            }

            return true;
        }

        _circuitBreaker.RecordSuccess(circuitTarget);
        var totalDurationMs = stopwatch.ElapsedMilliseconds;

        try
        {
            // Bedrock's native tap is out of scope (docs/router/openai-format-usage-accuracy-plan.md §4.2):
            // its streaming chunks aren't SSE-framed, so the same capture approach doesn't apply. Always
            // null here - telemetry falls back to parsing the translated "openai"-shaped bytes, unchanged
            // from before this plan.
            await PublishTelemetryAsync(context, route, requestedModelName, isFallback, "openai", rewrittenBody, capturedResponseBytes, nativeResponseBytes: null, isStreamingRequest, latencyToHeadersMs, totalDurationMs, StatusCodes.Status200OK, context.RequestAborted, upstreamHeaders: null, tailScanner: tailScanner, taskEmbedding: taskEmbedding, routerTokens: routerTokens, resolutionReason: resolutionReason, isExploratory: isExploratory, propensity: propensity, classification: classification, taskText: taskText, dimBestModel: dimBestModel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish routing telemetry; the forwarded response was unaffected.");
        }

        return true;
    }

    /// <summary>
    /// Consumes each already-decoded chunk from a Bedrock <c>InvokeModelWithResponseStream</c> response
    /// (the AWS SDK has already parsed its binary event-stream framing into discrete
    /// <see cref="PayloadPart"/> items), translates each to an OpenAI-shaped SSE chunk, writes it to the
    /// client as it's produced, and captures up to <paramref name="captureCap"/> bytes for telemetry -
    /// mirrors <see cref="TranslateAndCaptureStreamAsync"/>'s role for the raw-HTTP path.
    /// </summary>
    private async Task<(byte[] Captured, IncrementalUsageScanner? TailScanner)> TranslateAndCaptureBedrockStreamAsync(IBedrockPayloadTranslator translator, ResponseStream body, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        var chunkTranslator = translator.CreateBedrockStreamChunkTranslator();
        using var capture = new MemoryStream();
        IncrementalUsageScanner? tailScanner = null;

        async Task EmitAsync(byte[] translated)
        {
            if (translated.Length == 0)
            {
                return;
            }

            await destination.WriteAsync(translated, cancellationToken);
            await destination.FlushAsync(cancellationToken);

            var remainingCapacity = captureCap - (int)capture.Length;
            if (remainingCapacity > 0)
            {
                await capture.WriteAsync(translated.AsMemory(0, Math.Min(translated.Length, remainingCapacity)), cancellationToken);
            }

            if (remainingCapacity < translated.Length)
            {
                // Same lazy-allocation reasoning as CopyAndCaptureAsync: only worth the tail window once
                // the head-capped capture has actually lost bytes, and the boundary-crossing chunk itself
                // still needs to land in the scanner in full.
                tailScanner ??= new IncrementalUsageScanner();
                tailScanner.Append(translated);
            }
        }

        try
        {
            await foreach (var streamEvent in body.WithCancellation(cancellationToken))
            {
                if (streamEvent is PayloadPart part)
                {
                    await EmitAsync(chunkTranslator.TranslateChunk(part.Bytes.ToArray()));
                }

                // A non-PayloadPart event (e.g. a future AWS-added event kind this codebase doesn't yet
                // know about) carries nothing client-visible today - skipped rather than guessed at.
            }

            await EmitAsync(chunkTranslator.Flush());
        }
        catch (AnthropicStreamException ex)
        {
            // An embedded error inside a Bedrock Claude chunk (AnthropicOnBedrockStreamChunkTranslator
            // reuses AnthropicStreamTranslator's per-event handling, which throws on one) - truncate the
            // client stream, mirroring native Anthropic's and Gemini's mid-stream-error handling.
            _logger.LogWarning(ex, "Bedrock Claude streaming response terminated by an error event; the client stream was truncated.");
        }
        catch (ModelStreamErrorException ex)
        {
            // An AWS-level mid-stream error (surfaced by the SDK itself while enumerating ResponseStream,
            // not an embedded provider-JSON error) - same truncation response as the case above.
            _logger.LogWarning(ex, "Bedrock streaming response terminated by a ModelStreamErrorException; the client stream was truncated.");
        }
        catch (Exception ex) when (IsStreamAbort(ex))
        {
            _logger.LogWarning(ex, "Streaming response to the client was interrupted (client disconnected, or the connection was aborted); the forward was terminated early.");
        }

        return (capture.ToArray(), tailScanner);
    }

    /// <summary>Writes a client-facing error envelope for a failed upstream call (a Bedrock SDK failure, or an exhausted transport-outage cascade), matching <see cref="WriteModelNotFoundResponseAsync"/>'s shape. Defaults to 502 (the request was valid; the upstream call failed) rather than 400 (a malformed/unknown-model client request); <paramref name="statusCode"/> lets a caller override this - e.g. 401 for a Bedrock credential-resolution failure treated like the HTTP path's 401 handling. Callers pass a client-safe <paramref name="errorMessage"/> - never a raw transport-exception message, which can leak infrastructure detail.</summary>
    private static async Task WriteUpstreamErrorResponseAsync(HttpContext context, string errorMessage, int statusCode = StatusCodes.Status502BadGateway)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            error = new
            {
                message = errorMessage,
                type = "upstream_error",
                param = (string?)null,
                code = statusCode.ToString()
            }
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }

    /// <summary>
    /// Resolves the request's session and turn number, extracts usage and cost from the captured response,
    /// and publishes a telemetry record for the completed proxy call.
    ///
    /// <para>
    /// <c>nativeResponseBytes</c> is a captured pre-translation copy of the upstream response, when one was
    /// taken (see <see cref="CapturedResponse"/> and <c>docs/router/openai-format-usage-accuracy-plan.md</c>
    /// §4). When non-null and non-empty, usage and response-text extraction read from these bytes under
    /// <c>route</c>'s own provider key instead of from <c>capturedResponseBytes</c> under
    /// <c>telemetryShapeProvider</c> - the native shape is immune to translation lossiness (e.g. dropped
    /// Anthropic cache-token fields), so it is always preferred when available.
    /// </para>
    /// <para>
    /// <c>resolutionReason</c> is the <see cref="RoutingSubstitutionReason"/> as known when
    /// <c>RequestInterceptor</c> resolved this request, before any transport-level failover. Overridden
    /// by <see cref="RoutingSubstitutionReason.Failover"/> when <c>isFallback</c> is <see langword="true"/>
    /// - see <see cref="ResolveSubstitutionReason"/>.
    /// </para>
    /// </summary>
    private async Task PublishTelemetryAsync(
        HttpContext context,
        ResolvedModelRoute route,
        string requestedModelName,
        bool isFallback,
        string telemetryShapeProvider,
        byte[] rewrittenRequestBody,
        byte[] capturedResponseBytes,
        byte[]? nativeResponseBytes,
        bool isStreaming,
        long latencyToHeadersMs,
        long totalDurationMs,
        int statusCode,
        CancellationToken cancellationToken,
        System.Net.Http.Headers.HttpResponseHeaders? upstreamHeaders = null,
        IncrementalUsageScanner? tailScanner = null,
        float[]? taskEmbedding = null,
        int routerTokens = 0,
        RoutingSubstitutionReason resolutionReason = RoutingSubstitutionReason.None,
        bool isExploratory = false,
        double propensity = 1.0,
        RequestClassification? classification = null,
        string? taskText = null,
        string? dimBestModel = null)
    {
        var requestBody = TryParseJsonObject(rewrittenRequestBody);
        var resolvedSessionId = _sessionIdResolver.Resolve(context.Request.Headers, requestBody);

        var isSynthesized = resolvedSessionId is null;
        // No explicit session id found: fall back to matching this request's "messages" array against
        // previously-tracked conversations (see MessageHistoryContinuityMatcher), which itself falls
        // back to a fresh id if nothing matches - covers clients (e.g. GitHub Copilot's OpenAI-compatible
        // model providers) that send no session identifier of any kind, not even under an unrecognized name.
        var sessionId = resolvedSessionId ?? _continuityMatcher.MatchOrTrack(requestBody?["messages"] as JsonArray);
        var turnNumber = _turnTracker.NextTurn(sessionId);

        if (isSynthesized)
        {
            if (turnNumber == 1)
            {
                // No known session-id convention (see SessionIdResolver) matched anything on this
                // request, and no tracked conversation's message history was a prefix of this request's
                // messages either, so this is a brand-new tracked session. Logs header *names* (never
                // values, to avoid leaking auth tokens/cookies) and top-level body *keys* (never values)
                // so an unrecognized client's actual conventions can be spotted and, if there's a stable
                // per-conversation field under a different name, added to SessionIdResolver.
                _logger.LogDebug(
                    "No session id found on request to {Path}, and no tracked conversation's message " +
                    "history matched; started tracking new session {SessionId}. Request header names: " +
                    "[{HeaderNames}]. Top-level body keys: [{BodyKeys}].",
                    SanitizeForLog(context.Request.Path.ToString()),
                    SanitizeForLog(sessionId),
                    string.Join(", ", context.Request.Headers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(SanitizeForLog)),
                    requestBody is null ? "(not a JSON object)" : string.Join(", ", requestBody.Select(kv => SanitizeForLog(kv.Key))));
            }
            else
            {
                _logger.LogDebug(
                    "No session id found on request to {Path}, but its message history matched tracked " +
                    "session {SessionId}; treating as turn {TurnNumber}.",
                    SanitizeForLog(context.Request.Path.ToString()),
                    SanitizeForLog(sessionId),
                    turnNumber);
            }
        }
        else
        {
            _logger.LogDebug("Resolved session {SessionId}, turn {TurnNumber}.", SanitizeForLog(sessionId), turnNumber);
        }

        // The client's literal requested model (docs/router/orchestrator-live-path-plan.md §M2.2) - always
        // distinct from route.ModelName (the model that served) when any substitution or failover
        // occurred; substitutionReason below names why. isFallback additionally tells the dashboard the
        // resolved primary specifically was bypassed at the transport layer. See the failover loop in
        // InvokeAsync. This is the infrastructure-outage cascade, not the paper's Verifier-driven
        // semantic re-routing.
        var requestedModel = requestedModelName;

        // Failover (a transport-level bypass ProxyMiddleware's own loop discovered) always wins over
        // whatever RequestInterceptor knew at resolution time: if isFallback is true, the primary that
        // resolutionReason describes was never actually served, so Failover is the more accurate account
        // of why route differs from requestedModel.
        var substitutionReason = ResolveSubstitutionReason(isFallback, resolutionReason);

        int? promptTokens = null;
        int? completionTokens = null;
        int? cacheCreationTokens = null;
        int? cacheReadTokens = null;
        decimal? estimatedCostUsd = null;
        var costConfidence = CostConfidence.NoUsage;

        // Telemetry stops depending on translation fidelity: when a native (pre-translation) capture was
        // taken, it is parsed under the provider's own key instead of the translated bytes under
        // telemetryShapeProvider - the native shape carries fields (e.g. Anthropic cache tokens) that
        // TranslateResponse/EmitChunk currently drop on the way to the OpenAI-shaped client response (see
        // docs/router/openai-format-usage-accuracy-plan.md §1).
        // Explicit typed locals plus a plain if/else, rather than a ternary/tuple-deconstruction one-liner,
        // so usageShapeBytes's static type is byte[] (not the byte[]? a ternary over nativeResponseBytes
        // would otherwise infer) - the `is { Length: > 0 }` pattern below already proves it non-null on the
        // branch that assigns it.
        string usageShapeProvider;
        byte[] usageShapeBytes;
        bool usedNativeBytes;
        if (nativeResponseBytes is { Length: > 0 } nonEmptyNativeBytes)
        {
            usedNativeBytes = true;
            usageShapeProvider = route.Provider;
            usageShapeBytes = nonEmptyNativeBytes;
        }
        else
        {
            usedNativeBytes = false;
            usageShapeProvider = telemetryShapeProvider;
            usageShapeBytes = capturedResponseBytes;
        }

        var usageExtracted = _usageExtractor.TryExtractUsage(usageShapeProvider, isStreaming, usageShapeBytes, out var usage);
        if (!usageExtracted && usedNativeBytes)
        {
            // The native capture and the translated/client-shape capture are independently truncated at
            // MaxCapturedResponseBytes (see CopyAndCaptureAsync), so a large response can cut the native
            // bytes off before the usage block - often the last thing to arrive in a streamed response -
            // while the other capture still has it. Falling back recovers usage/cost for budget enforcement
            // and the spend ledger instead of recording nothing purely because the preferred capture was
            // the one that got cut off. usageShapeProvider/usageShapeBytes are reassigned too (not just the
            // local usage result), so the response-text extraction below - which reuses the same pair -
            // benefits from the same fallback instead of independently failing against the truncated bytes.
            usageExtracted = _usageExtractor.TryExtractUsage(telemetryShapeProvider, isStreaming, capturedResponseBytes, out usage);
            if (usageExtracted)
            {
                usageShapeProvider = telemetryShapeProvider;
                usageShapeBytes = capturedResponseBytes;
            }
        }

        // Last resort (§5.11): both captures above are head-capped at MaxCapturedResponseBytes, so a
        // response larger than the cap can cut off entirely before reaching its usage block (typically the
        // final SSE event of a streamed response). tailScanner retained a trailing window over the stream
        // independent of that cap - deliberately NOT reassigning usageShapeProvider/usageShapeBytes here,
        // unlike the native-capture fallback above: the response-text extraction below reuses that same
        // pair, and a tail-only buffer holds only the end of a long streamed answer, which would make
        // ResponseSummary a worse (truncated-from-the-front) result than what the head-capped bytes already
        // gave it - the tail is only trustworthy for recovering the trailing usage numbers, not the text.
        if (!usageExtracted && tailScanner is not null)
        {
            usageExtracted = tailScanner.TryExtractUsage(telemetryShapeProvider, isStreaming, _usageExtractor, out usage);
        }

        if (!usageExtracted)
        {
            // Tagged with route.Provider (the real upstream provider), not telemetryShapeProvider: for a
            // translated provider (gemini, ollama), telemetryShapeProvider is forced to "openai" (the
            // shape the extractor parses, not who actually served the request), and the metric's own doc
            // comment promises per-provider attribution so a regression in one translator's output is
            // distinguishable from another's.
            UsageMetrics.ExtractionFailedTotal.Add(1, new KeyValuePair<string, object?>("provider", route.Provider));
            _logger.LogDebug(
                "Could not extract usage for provider {Provider} (telemetry shape {TelemetryShapeProvider}, streaming: {IsStreaming}); no cost/token telemetry will be recorded for this request.",
                SanitizeForLog(route.Provider),
                SanitizeForLog(telemetryShapeProvider),
                isStreaming);
        }

        if (usageExtracted)
        {
            promptTokens = usage.PromptTokens;
            completionTokens = usage.CompletionTokens;
            cacheCreationTokens = usage.CacheCreationTokens;
            cacheReadTokens = usage.CacheReadTokens;

            // A free provider (a local Ollama runtime, say) has a *known* price of zero. A paid model's
            // price comes from the auto-refreshed price catalog (docs/router/model-price-catalog.md) via
            // _priceLookup; when the catalog has no fresh price for it (lookup returns null, or none was
            // injected), cost stays null - unknown, never silently zero. Zero and unknown are different
            // answers and must not collapse into one. Both real branches run through EstimateCost so there is
            // exactly one cost formula. The catalog keys prices on the client-facing (ModelName, provider)
            // once D3 alias resolution has mapped each source's own naming onto it at ingest
            // (docs/router/d3-alias-resolution.md), so we look up route.ModelName - a model the catalog has no
            // resolved price for simply yields null here, the safe "unknown" outcome.
            // §5.6: the cost-confidence label travels alongside the cost itself, so a caller never has to
            // re-derive from EstimatedCostUsd alone whether "$0" means free, unknown, or an approximate
            // catalog match - see docs/router/token-tracking-improvements.md §5.6.
            if (route.IsFree)
            {
                estimatedCostUsd = ModelPrice.Free.EstimateCost(usage);
                costConfidence = CostConfidence.Exact;
            }
            else if (_priceLookup?.TryGetPrice(new ModelKey(ModelName: route.ModelName, Provider: route.Provider)) is { } price)
            {
                estimatedCostUsd = price.EstimateCost(usage, out var usedCacheRateFallback);
                costConfidence = price.IsApproximateMatch || usedCacheRateFallback
                    ? CostConfidence.CatalogApproximate
                    : CostConfidence.Catalog;
            }
            else
            {
                costConfidence = CostConfidence.Unknown;
            }
        }

        // Best-effort, like every other telemetry side-effect on this path - see SpendTracker's own
        // internal try/catch around its file write. Recorded even when usage/cost couldn't be
        // determined, so the running request count stays accurate; it just contributes zero cost/tokens.
        // Uses CancellationToken.None rather than the request's cancellationToken (context.RequestAborted):
        // this runs after the response has already been fully sent, and RequestAborted fires the moment the
        // client disconnects - which for a streaming response happens right as the client finishes reading
        // it - so recording must not be cancellable by the request's own lifetime or it gets silently dropped.
        // Attributed to route.ModelName - the model that actually served (M2.3, decided: a value fix, not
        // a schema change) - not requestedModel, which on an auto-majority deployment would otherwise file
        // nearly all spend under the literal string "auto".
        await _spendTracker.RecordAsync(route.ModelName, promptTokens, completionTokens, estimatedCostUsd, CancellationToken.None).ConfigureAwait(false);

        // Attribute this request's usage to the provider that actually served it (route.Provider is the
        // post-failover, post-budget-skip winner), so per-provider monthly spend and the Governance budget
        // bars stay accurate. Best-effort and self-guarding, exactly like the spend tracker above - same
        // CancellationToken.None reasoning applies. Gated on usageExtracted (unlike the spend tracker
        // above): a zero-usage row here would advance LastUsageAtUtc and make the admin UI report a
        // misleading "last recorded" time for a provider whose response simply carried no usage block.
        if (_budgetStore is not null && usageExtracted)
        {
            await _budgetStore.RecordUsageAsync(
                route.Provider,
                estimatedCostUsd,
                promptTokens,
                completionTokens,
                cacheCreationTokens,
                cacheReadTokens,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
        }

        // The durable usage ledger (docs/router/token-tracking-implementation-plan.md Phase 2): recorded
        // immediately after the budget store, under the same best-effort/CancellationToken.None reasoning,
        // and gated on usageExtracted for the same "don't advance state on a zero-usage row" reason. The
        // dedup key prefers the upstream's own request id (request-id/x-request-id, read from the response
        // headers already in hand here) over the composite hash, so a replayed publish of the same request
        // collides with its own earlier write instead of double-counting.
        if (_usageLedger is not null && usageExtracted)
        {
            var ledgerEntry = new UsageLedgerEntry(
                SessionId: sessionId,
                TurnNumber: turnNumber,
                Provider: route.Provider,
                // The model that served (M2.3) - matches this column's documented meaning
                // (docs/router/agent-cost-tracking.md: "the model this spend belongs to"), not the one
                // lined up first when a substitution or failover occurred.
                RequestedModel: route.ModelName,
                ResolvedModel: route.ProviderModelId,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                CacheCreationTokens: cacheCreationTokens,
                CacheReadTokens: cacheReadTokens,
                EstimatedCostUsd: estimatedCostUsd,
                CostConfidence: costConfidence,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                RequestId: ExtractUpstreamRequestId(upstreamHeaders));
            await _usageLedger.RecordAsync(ledgerEntry, CancellationToken.None).ConfigureAwait(false);
        }

        // §5.12: published unconditionally (not gated on _usageLedger being configured) so an operator who
        // wants OTLP/Prometheus export but not the SQLite ledger still gets it. Creating a Meter/instrument
        // and calling Add on it is cheap with no listener attached - only an actually-configured OTLP
        // exporter (a hosting-level decision, not this class's) turns these into real exported metrics.
        if (usageExtracted)
        {
            EmitUsageMetrics(route.Provider, requestedModel, promptTokens, completionTokens, cacheCreationTokens, cacheReadTokens, estimatedCostUsd);
        }

        // Extracted once and reused below for the quality-ingress prompt - both read the same newest-user-
        // message text off the same already-parsed requestBody, so a second walk of its messages array
        // would just repeat the first.
        var newestUserMessage = RequestTextExtractor.ExtractNewestUserMessage(requestBody);
        var requestSummary = TextTruncator.Truncate(newestUserMessage);
        var responseSummary = _responseTextExtractor.TryExtractText(usageShapeProvider, isStreaming, usageShapeBytes, out var responseText)
            ? TextTruncator.Truncate(responseText)
            : null;

        // A stable id shared by this telemetry event and any off-path quality signal derived from the same
        // response, so a dashboard can join the two.
        var correlationId = FormattableString.Invariant($"{sessionId}:{turnNumber}");

        // docs/router/live-feedback-learning-plan.md Phase 2c: this is the earliest point the correlation
        // id a later-arriving QualityResult carries is actually known - RequestInterceptor.ResolveModelRouteAsync
        // computed taskEmbedding well before session/turn resolution ran, so it could not key this itself.
        // Recorded here, immediately once both halves exist, rather than passed to RequestInterceptor.
        if (taskEmbedding is not null)
        {
            _pendingTaskEmbeddingCache?.Set(correlationId, taskEmbedding);
        }

        // docs/router/self-organizing-classification-plan.md Phase T1c: mirrors the embedding cache
        // Set above exactly - same correlation id, same "this is the earliest point the value is known
        // alongside the correlation id" reasoning - so EmbeddingMemoryScoreObserver can recover the real
        // cost and provenance once the verifier score arrives instead of writing cost 0.0 / certain
        // non-exploratory provenance unconditionally.
        _pendingRequestCostCache?.Set(correlationId, estimatedCostUsd ?? 0m);
        _pendingRequestProvenanceCache?.Set(correlationId, isExploratory, propensity, classification?.Dimension);

        // docs/router/geval-shadow-scoring-plan.md §Raw-text preservation: the response text is already in
        // hand from the TryExtractText call above (responseSummary's source) - this adds retention only,
        // for JudgeShadowScoreObserver's later-arriving background job to recover by TryTake. Gated on
        // extraction having actually succeeded (responseText is only assigned when TryExtractText returns
        // true) and on the judge being switched on right now - read from the monitor, not captured once,
        // exactly like the EnableAdaptiveRouting gate below. That live read is the whole point here: the
        // judge toggle is what authorizes retaining raw response text in memory at all, so switching it off
        // has to stop retention immediately rather than at the next restart.
        if (responseSummary is not null && (_judgeOptionsMonitor?.CurrentValue.Enabled ?? false))
        {
            _pendingResponseTextCache?.Set(correlationId, responseText);
        }

        // docs/router/self-organizing-classification-plan.md Phase T1a/T1b: the transcript store's single
        // insert. Best-effort and off the hot path in spirit (the response has already been fully sent to
        // the client by this point) - gated on TranscriptOptions.Enabled so a disabled install creates no
        // table and writes nothing, and wrapped in its own try/catch so a transcript-store failure can
        // never surface as a routing failure, matching every other telemetry side-effect in this method.
        // Phase T6 adds RoutingOptions.EnableAdaptiveRouting as a second, live gate - read from the
        // monitor (not captured once) so toggling it stops or resumes writes without a restart.
        if (_transcriptStore is not null && (_routingOptionsMonitor?.CurrentValue.EnableAdaptiveRouting ?? false))
        {
            try
            {
                await _transcriptStore.InsertAsync(
                    new TranscriptRecord(
                        Id: 0,
                        CorrelationId: correlationId,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        RequestedModel: requestedModelName,
                        RoutedModel: route.ModelName,
                        Dimension: classification?.Dimension,
                        Difficulty: classification?.Difficulty,
                        Language: classification?.Language,
                        IsUtility: classification?.IsUtility ?? false,
                        PromptText: taskText,
                        ResponseText: responseText,
                        Score: null,
                        Cost: estimatedCostUsd,
                        IsExploratory: isExploratory,
                        Propensity: propensity,
                        InputTokens: promptTokens,
                        OutputTokens: completionTokens,
                        MemoryEntryId: null,
                        DimBestModel: dimBestModel),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to write a transcript row for correlation {CorrelationId}; continuing without it.", correlationId);
            }
        }

        // What routing this request cost us, charged at the self-hosted rate (research-doc §5.1: TotTok is
        // router + model, so routing overhead is the router's to carry). Kept separate from
        // estimatedCostUsd - which is what the upstream provider charged - because a savings figure has to
        // be net of this, and folding the two together would make that impossible to unwind downstream.
        var routerCostUsd = routerTokens / 1_000_000m * _selfHostedRouterPricePerMillionTokens;

        var telemetryEvent = new RoutingTelemetryEvent(
            SessionId: sessionId,
            TurnNumber: turnNumber,
            IsSessionSynthesized: isSynthesized,
            RequestedModel: requestedModel,
            ResolvedModel: route.ProviderModelId,
            Provider: route.Provider,
            IsFallback: isFallback,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            EstimatedCostUsd: estimatedCostUsd,
            IsStreaming: isStreaming,
            LatencyToHeadersMs: latencyToHeadersMs,
            TotalDurationMs: totalDurationMs,
            StatusCode: statusCode,
            TimestampUtc: DateTimeOffset.UtcNow,
            RoutedModel: route.ModelName,
            CacheCreationTokens: cacheCreationTokens,
            CacheReadTokens: cacheReadTokens,
            CostConfidence: costConfidence,
            RequestSummary: requestSummary,
            ResponseSummary: responseSummary,
            CorrelationId: correlationId,
            RouterTokens: routerTokens,
            RouterCostUsd: routerCostUsd,
            SubstitutionReason: substitutionReason);

        await _telemetryPublisher.PublishAsync(telemetryEvent, cancellationToken);

        // Off-path, best-effort: hand the completed response to the quality verifier for static and
        // scoring. The ingress samples, extracts, and enqueues without blocking; it never throws. Reuses
        // the already-extracted (untruncated) response text so no second copy of the body is made.
        if (_qualityIngress is not null && !string.IsNullOrEmpty(responseText))
        {
            _qualityIngress.TryIngest(new QualityIngestContext(
                ResponseText: responseText,
                Prompt: newestUserMessage ?? string.Empty,
                Model: requestedModel,
                CorrelationId: correlationId,
                SessionId: sessionId));
        }
    }

    /// <summary>
    /// Resolves the <see cref="RoutingSubstitutionReason"/> actually reported for a served request:
    /// <see cref="RoutingSubstitutionReason.Failover"/> when <paramref name="isFallback"/> is
    /// <see langword="true"/> (the candidate <c>RequestInterceptor</c> lined up first was attempted and
    /// failed at the transport layer, so whatever reason it computed no longer describes what happened),
    /// otherwise <paramref name="resolutionReason"/> unchanged.
    /// </summary>
    private static RoutingSubstitutionReason ResolveSubstitutionReason(bool isFallback, RoutingSubstitutionReason resolutionReason) =>
        isFallback ? RoutingSubstitutionReason.Failover : resolutionReason;

    /// <summary>
    /// Publishes one request's usage to <see cref="UsageMetrics"/> (§5.12): a token count per non-zero
    /// dimension, cost when known, and <see cref="UsageMetrics.UnpricedRequestsTotal"/> when it isn't -
    /// mirroring the ledger's own "unknown is not zero" distinction rather than defaulting a missing cost
    /// to 0.0 in the exported metric.
    /// </summary>
    private static void EmitUsageMetrics(string provider, string model, int? promptTokens, int? completionTokens, int? cacheCreationTokens, int? cacheReadTokens, decimal? estimatedCostUsd)
    {
        void AddTokens(int? value, string kind)
        {
            if (value is > 0)
            {
                UsageMetrics.TokensTotal.Add(
                    value.Value,
                    new KeyValuePair<string, object?>("provider", provider),
                    new KeyValuePair<string, object?>("model", model),
                    new KeyValuePair<string, object?>("kind", kind));
            }
        }

        AddTokens(promptTokens, "prompt");
        AddTokens(completionTokens, "completion");
        AddTokens(cacheCreationTokens, "cache_creation");
        AddTokens(cacheReadTokens, "cache_read");

        if (estimatedCostUsd is decimal cost)
        {
            UsageMetrics.CostUsdTotal.Add(
                (double)cost,
                new KeyValuePair<string, object?>("provider", provider),
                new KeyValuePair<string, object?>("model", model));
        }
        else
        {
            UsageMetrics.UnpricedRequestsTotal.Add(1, new KeyValuePair<string, object?>("provider", provider));
        }
    }

    /// <summary>
    /// Strips CR/LF from a value that originated from the client (request path, header names, JSON body
    /// keys, a client-supplied session id) before it's placed in a log message template. Without this, a
    /// crafted value could inject newlines into a text-rendering log sink and forge what looks like
    /// additional, fabricated log entries (CodeQL: "Log entries created from user input" / log forging,
    /// CWE-117). Chained <see cref="string.Replace(string, string)"/> calls directly on the tainted value
    /// - rather than e.g. a hand-rolled character loop - is the sanitizer shape CodeQL's data-flow
    /// analysis recognizes as breaking the taint path from source to sink.
    /// </summary>
    private static string SanitizeForLog(string? value) =>
        value?.Replace("\r", " ").Replace("\n", " ") ?? string.Empty;

    /// <summary>
    /// Reads the upstream's own request id off <paramref name="headers"/> - <c>request-id</c> (Anthropic)
    /// or, failing that, <c>x-request-id</c> (the more common convention) - for
    /// <see cref="UsageLedgerEntry.RequestId"/>. Returns <see langword="null"/> when
    /// <paramref name="headers"/> is absent (the Bedrock invocation path has no HTTP response headers to
    /// read) or neither header was sent, in which case <see cref="Telemetry.UsageLedger"/> falls back to
    /// its composite dedup key.
    /// </summary>
    private static string? ExtractUpstreamRequestId(System.Net.Http.Headers.HttpResponseHeaders? headers)
    {
        if (headers is null)
        {
            return null;
        }

        if (headers.TryGetValues("request-id", out var requestIdValues))
        {
            return requestIdValues.FirstOrDefault();
        }

        if (headers.TryGetValues("x-request-id", out var xRequestIdValues))
        {
            return xRequestIdValues.FirstOrDefault();
        }

        return null;
    }

    /// <summary>Attempts to parse the given bytes as a JSON object, returning null if they are not valid JSON or not an object.</summary>
    private static JsonObject? TryParseJsonObject(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Determines whether the (OpenAI-shaped) request body asked for a streaming response, i.e. its
    /// top-level <c>stream</c> field is <see langword="true"/>. Used only for translated providers, to
    /// pick the streaming vs non-streaming upstream URL and response path.
    /// </summary>
    private static bool IsStreamingRequest(byte[] requestBody)
    {
        try
        {
            return JsonNode.Parse(requestBody) is JsonObject obj &&
                obj["stream"] is JsonValue value &&
                value.TryGetValue<bool>(out var stream) &&
                stream;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the entire native (non-streaming) upstream body, runs it through the translator into
    /// OpenAI's shape, writes the translated bytes to the client, and returns both the translated bytes
    /// (capped, for the client-shape parsers) and - for a provider <see cref="UsageExtractor.SupportsNativeShape"/>
    /// recognizes - a second capped copy of the pre-translation native body, for the native telemetry tap
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4.1). The native body was already fully
    /// materialized in memory to call <see cref="IPayloadTranslator.TranslateResponse"/>, so capturing it
    /// costs nothing extra beyond the capped copy.
    /// </summary>
    private async Task<CapturedResponse> TranslateAndCaptureBufferedAsync(IPayloadTranslator translator, Stream source, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        try
        {
            using var upstream = new MemoryStream();
            await source.CopyToAsync(upstream, cancellationToken);
            var nativeBytes = upstream.ToArray();

            var translated = translator.TranslateResponse(nativeBytes);
            await destination.WriteAsync(translated, cancellationToken);

            var clientShapeBytes = translated.Length <= captureCap ? translated : translated[..captureCap];
            byte[]? capturedNativeBytes = UsageExtractor.SupportsNativeShape(translator.Provider)
                ? (nativeBytes.Length <= captureCap ? nativeBytes : nativeBytes[..captureCap])
                : null;

            // Only worth the ~64KB tail-window allocation when the head-capped clientShapeBytes above
            // actually lost data - a response that fits within captureCap is already fully captured, so
            // TryExtractUsage's primary parse never needs the tail fallback.
            IncrementalUsageScanner? tailScanner = null;
            if (translated.Length > captureCap)
            {
                tailScanner = new IncrementalUsageScanner();
                tailScanner.Append(translated);
            }

            return new CapturedResponse(clientShapeBytes, capturedNativeBytes, tailScanner);
        }
        catch (Exception ex) when (IsStreamAbort(ex) && cancellationToken.IsCancellationRequested)
        {
            // Unlike TranslateAndCaptureStreamAsync, nothing has necessarily reached the client yet here -
            // the whole upstream body is read into memory before the one translated write - so this only
            // fails open (swallows and reports an empty capture) for a genuine client abort, where there is
            // truly no one left to answer. An upstream I/O failure that is NOT the client leaving (checked
            // via cancellationToken.IsCancellationRequested, mirroring IsTransportOutage's identical
            // distinction for the SendAsync case) is deliberately left to propagate, so the caller can still
            // turn it into a real 502 instead of silently committing a 200 with an empty body.
            _logger.LogWarning(ex, "Buffered response to the client was interrupted by a client disconnect; the forward was terminated early.");
            return new CapturedResponse([], null, null);
        }
    }

    /// <summary>
    /// Streams the native SSE upstream through a per-request <see cref="IStreamTranslator"/>, writing
    /// each translated OpenAI-shaped chunk to the client as it is produced and capturing up to
    /// <paramref name="captureCap"/> bytes of the translated stream for telemetry - plus, for a provider
    /// <see cref="UsageExtractor.SupportsNativeShape"/> recognizes, a second capped accumulation of the raw
    /// upstream SSE bytes (before they enter the stream translator), for the native telemetry tap
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4.1) - the one genuinely new buffer this
    /// plan adds, capped like every other capture here. An embedded provider error throws
    /// <see cref="GeminiStreamException"/> mid-stream (mirroring LiteLLM): the forward is then terminated -
    /// the client sees a truncated stream with no <c>[DONE]</c>, since a 200 OK and earlier chunks have
    /// already been committed to the wire and the status can no longer change.
    /// </summary>
    private async Task<CapturedResponse> TranslateAndCaptureStreamAsync(IPayloadTranslator translator, Stream source, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        var streamTranslator = translator.CreateStreamTranslator();
        var captureNativeBytes = UsageExtractor.SupportsNativeShape(translator.Provider);
        using var capture = new MemoryStream();
        using var nativeCapture = captureNativeBytes ? new MemoryStream() : null;
        IncrementalUsageScanner? tailScanner = null;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        async Task EmitAsync(byte[] translated)
        {
            if (translated.Length == 0)
            {
                return;
            }

            await destination.WriteAsync(translated, cancellationToken);
            await destination.FlushAsync(cancellationToken);

            var remainingCapacity = captureCap - (int)capture.Length;
            if (remainingCapacity > 0)
            {
                await capture.WriteAsync(translated.AsMemory(0, Math.Min(translated.Length, remainingCapacity)), cancellationToken);
            }

            if (remainingCapacity < translated.Length)
            {
                // Same lazy-allocation reasoning as CopyAndCaptureAsync: only worth the tail window once
                // the head-capped capture has actually lost bytes, and the boundary-crossing chunk itself
                // still needs to land in the scanner in full.
                tailScanner ??= new IncrementalUsageScanner();
                tailScanner.Append(translated);
            }
        }

        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                if (nativeCapture is not null)
                {
                    var remainingNativeCapacity = captureCap - (int)nativeCapture.Length;
                    if (remainingNativeCapacity > 0)
                    {
                        await nativeCapture.WriteAsync(buffer.AsMemory(0, Math.Min(bytesRead, remainingNativeCapacity)), cancellationToken);
                    }
                }

                await EmitAsync(streamTranslator.Push(buffer.AsSpan(0, bytesRead)));
            }

            await EmitAsync(streamTranslator.Flush());
        }
        catch (GeminiStreamException ex)
        {
            _logger.LogWarning(ex, "Gemini streaming response terminated by an embedded provider error; the client stream was truncated.");
        }
        catch (AnthropicStreamException ex)
        {
            _logger.LogWarning(ex, "Anthropic streaming response terminated by an error event; the client stream was truncated.");
        }
        catch (Exception ex) when (IsStreamAbort(ex))
        {
            _logger.LogWarning(ex, "Streaming response to the client was interrupted (client disconnected, or the connection was aborted); the forward was terminated early.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new CapturedResponse(capture.ToArray(), nativeCapture?.ToArray(), tailScanner);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> unchanged (the client-facing
    /// forward), while also capturing up to <paramref name="captureCap"/> bytes for telemetry usage
    /// parsing, plus a trailing <see cref="IncrementalUsageScanner"/> window independent of that cap
    /// (§5.11). The capture never delays or alters what reaches <paramref name="destination"/> - it's an
    /// in-memory side copy of each chunk immediately after (not instead of) writing it downstream.
    /// </summary>
    private async Task<(byte[] Captured, IncrementalUsageScanner? TailScanner)> CopyAndCaptureAsync(Stream source, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        using var capture = new MemoryStream();
        IncrementalUsageScanner? tailScanner = null;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                // Unlike a translated response (whose EmitAsync helper flushes every emitted chunk), this
                // is the raw pass-through path for a provider with no translator (Ollama, raw OpenAI, an
                // OpenAI-compatible local server). Without an explicit flush, Kestrel can hold small,
                // infrequent writes - exactly what a slow local model's token-by-token SSE stream produces
                // - in its internal buffer instead of pushing them to the client promptly, so the client
                // sees no bytes at all until the connection eventually closes (or times out first).
                await destination.FlushAsync(cancellationToken);

                var remainingCapacity = captureCap - (int)capture.Length;
                if (remainingCapacity > 0)
                {
                    await capture.WriteAsync(buffer.AsMemory(0, Math.Min(bytesRead, remainingCapacity)), cancellationToken);
                }

                if (remainingCapacity < bytesRead)
                {
                    // This chunk pushes (or has already pushed) the head-capped capture past captureCap, so
                    // some/all of it would otherwise be lost - exactly when the tail scanner earns its
                    // allocation. A response that never exceeds the cap is already fully captured by
                    // `capture` above, so tailScanner would be dead weight for the common case. Checking
                    // remainingCapacity (computed before this chunk's write) rather than capture.Length
                    // after it ensures the very chunk that crosses the cap boundary is still captured in
                    // full here, not just the chunks after it.
                    tailScanner ??= new IncrementalUsageScanner();
                    tailScanner.Append(buffer.AsSpan(0, bytesRead));
                }
            }
        }
        catch (Exception ex) when (IsStreamAbort(ex))
        {
            // Mirrors TranslateAndCaptureStreamAsync's identical fail-open handling: the response has
            // already started, so there is nothing left to do but stop copying and return whatever was
            // captured so far.
            _logger.LogWarning(ex, "Streaming response to the client was interrupted (client disconnected, or the connection was aborted); the forward was terminated early.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (capture.ToArray(), tailScanner);
    }

    /// <summary>
    /// Builds the set of hop-by-hop header names to strip: the fixed RFC 7230 set, plus any additional header
    /// names nominated by a <c>Connection</c> header value (e.g. <c>Connection: Foo</c> makes <c>Foo</c> hop-by-hop).
    /// </summary>
    private static HashSet<string> GetHopByHopHeaderNames(IEnumerable<string>? connectionHeaderValues)
    {
        var names = new HashSet<string>(HopByHopHeaders, StringComparer.OrdinalIgnoreCase);

        if (connectionHeaderValues is null)
        {
            return names;
        }

        foreach (var value in connectionHeaderValues)
        {
            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(token);
            }
        }

        return names;
    }

    /// <summary>
    /// Determines whether a request targets the OpenAI-compatible model discovery endpoint
    /// (<c>GET /v1/models</c>), matched case-insensitively and with an optional trailing slash tolerated,
    /// since both conventions vary by client.
    /// </summary>
    private static bool IsModelsListRequest(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        string.Equals(request.Path.Value?.TrimEnd('/'), ModelsListPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the configured model list as an OpenAI-compatible <c>/v1/models</c> response, mirroring
    /// LiteLLM's behavior of answering this endpoint from local configuration rather than forwarding it
    /// upstream.
    /// </summary>
    private async Task WriteModelsListResponseAsync(HttpContext context)
    {
        var entries = _interceptor.ListAvailableModels()
            .Select(model => new ModelListEntry(model.ModelName, "model", 0, model.Provider))
            .ToList();

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new ModelsListResponse("list", entries)),
            context.RequestAborted);
    }

    /// <summary>
    /// A single entry in the <c>/v1/models</c> response, shaped to match OpenAI's model list schema.
    /// </summary>
    private sealed record ModelListEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("created")] long Created,
        [property: JsonPropertyName("owned_by")] string OwnedBy);

    /// <summary>
    /// The top-level <c>/v1/models</c> response envelope, shaped to match OpenAI's model list schema.
    /// </summary>
    private sealed record ModelsListResponse(
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("data")] IReadOnlyList<ModelListEntry> Data);

    /// <summary>
    /// Determines whether a request targets Ollama's native model discovery endpoint
    /// (<c>GET /api/tags</c>), matched case-insensitively and with an optional trailing slash tolerated,
    /// mirroring <see cref="IsModelsListRequest"/>.
    /// </summary>
    private static bool IsOllamaTagsRequest(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        string.Equals(request.Path.Value?.TrimEnd('/'), OllamaTagsPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the configured model list as an Ollama-native <c>/api/tags</c> response, so a client that
    /// added this proxy as an "Ollama" provider (e.g. Visual Studio's AI model picker) can discover the
    /// configured models the same way <see cref="WriteModelsListResponseAsync"/> answers the OpenAI-shaped
    /// discovery endpoint.
    /// </summary>
    private async Task WriteOllamaTagsResponseAsync(HttpContext context)
    {
        var entries = _interceptor.ListAvailableModels()
            .Select(model => new OllamaTagEntry(
                model.ModelName,
                model.ModelName,
                DateTimeOffset.UtcNow.ToString("O"),
                0,
                string.Empty,
                new OllamaTagDetails("gguf", string.Empty, string.Empty)))
            .ToList();

        // Debug, not Information: this fires on every poll from an Ollama-shaped client's model picker
        // (potentially frequent), and its outcome is fully captured by the response itself - this exists so
        // a trace can distinguish "answered locally from /api/tags" from the per-model routing path's own
        // logging, without adding noise at the default log level.
        _logger.LogDebug("Answered {Path} locally with {Count} configured model(s).", OllamaTagsPath, entries.Count);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new OllamaTagsResponse(entries)),
            context.RequestAborted);
    }

    /// <summary>
    /// The <c>details</c> object of one <see cref="OllamaTagEntry"/>, shaped to match Ollama's
    /// <c>/api/tags</c> schema. Only the fields Ollama always populates are set; format-specific fields the
    /// router has no equivalent for are left as ordinary defaults rather than fabricated.
    /// </summary>
    private sealed record OllamaTagDetails(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("family")] string Family,
        [property: JsonPropertyName("parameter_size")] string ParameterSize);

    /// <summary>
    /// A single entry in the <c>/api/tags</c> response, shaped to match Ollama's native model list schema.
    /// </summary>
    private sealed record OllamaTagEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("modified_at")] string ModifiedAt,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("details")] OllamaTagDetails Details);

    /// <summary>
    /// The top-level <c>/api/tags</c> response envelope, shaped to match Ollama's native model list schema.
    /// </summary>
    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<OllamaTagEntry> Models);

    /// <summary>
    /// Determines whether a request targets Ollama's native per-model detail endpoint (<c>POST /api/show</c>),
    /// matched case-insensitively and with an optional trailing slash tolerated, mirroring
    /// <see cref="IsOllamaTagsRequest"/>.
    /// </summary>
    private static bool IsOllamaShowRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        string.Equals(request.Path.Value?.TrimEnd('/'), OllamaShowPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Answers Ollama's native <c>POST /api/show</c> from local configuration instead of forwarding it
    /// upstream - see <see cref="OllamaShowPath"/>'s remarks for why forwarding it produces a confusing 400.
    /// Reads the requested model name out of the body's <c>{"model": "..."}</c> field the same way
    /// <see cref="RequestInterceptor.ResolveModelRouteAsync"/> does, and answers 404 for a model this proxy
    /// does not have configured - matching real Ollama's own behavior for an unknown model, which
    /// <see cref="Translation.ToolCalling.ModelDialectResolver.TryReadOllamaTemplateAsync"/> already relies
    /// on when probing a genuine Ollama endpoint.
    /// </summary>
    private async Task WriteOllamaShowResponseAsync(HttpContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        string? modelName = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject jsonObject &&
                jsonObject["model"] is JsonValue modelValue &&
                modelValue.TryGetValue<string>(out var value))
            {
                modelName = value;
            }
        }
        catch (JsonException)
        {
            // Falls through to the "model not found" response below, matching real Ollama's own behavior
            // for a request it cannot make sense of.
        }

        var model = modelName is null
            ? null
            : _interceptor.ListAvailableModels()
                .FirstOrDefault(m => string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

        if (model is null)
        {
            // Information, not Debug: unlike the tags poll above, this is the direct diagnostic signal for
            // exactly the failure mode this endpoint exists to prevent - a client naming a model this proxy
            // doesn't know, which without this local answer would instead fall through to the per-model
            // routing path and surface as a confusing 400 from whatever upstream that name happened to
            // resolve to (see OllamaShowPath's remarks).
            _logger.LogInformation(
                "Answered {Path} locally: unknown model '{ModelName}' requested; returning 404.",
                OllamaShowPath,
                SanitizeForLog(modelName));
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new OllamaErrorResponse($"model '{modelName}' not found")),
                context.RequestAborted);
            return;
        }

        _logger.LogDebug("Answered {Path} locally for model {Model}.", OllamaShowPath, SanitizeForLog(model.ModelName));

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new OllamaShowResponse(
                string.Empty,
                string.Empty,
                string.Empty,
                new OllamaTagDetails("gguf", string.Empty, string.Empty))),
            context.RequestAborted);
    }

    /// <summary>
    /// The <c>POST /api/show</c> response envelope, shaped to match Ollama's native per-model detail schema.
    /// Only the fields every Ollama install always populates are set; format-specific fields the router has
    /// no equivalent for (the model's literal chat template among them - see
    /// <see cref="Translation.ToolCalling.ModelDialectResolver"/> for where that is actually sourced from,
    /// when it is available at all) are left as ordinary defaults rather than fabricated.
    /// </summary>
    private sealed record OllamaShowResponse(
        [property: JsonPropertyName("modelfile")] string Modelfile,
        [property: JsonPropertyName("parameters")] string Parameters,
        [property: JsonPropertyName("template")] string Template,
        [property: JsonPropertyName("details")] OllamaTagDetails Details);

    /// <summary>An Ollama-shaped <c>{"error": "..."}</c> envelope, used for a <c>POST /api/show</c> naming an unknown model.</summary>
    private sealed record OllamaErrorResponse(
        [property: JsonPropertyName("error")] string Error);

    /// <summary>Writes a 400 response in an OpenAI-shaped error envelope for a request whose model could not be resolved.</summary>
    private static async Task WriteModelNotFoundResponseAsync(HttpContext context, string errorMessage)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            error = new
            {
                message = errorMessage,
                type = "invalid_request_error",
                param = "model",
                code = "400"
            }
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }

    /// <summary>
    /// Writes a 503 response in an OpenAI-shaped error envelope when the GUI system tray's kill switch
    /// (<see cref="Router.IRoutingGate"/>) has routing disabled. Matches
    /// <see cref="WriteModelNotFoundResponseAsync"/>'s envelope shape but as 503 (Service Unavailable): the
    /// request itself may be perfectly valid, it was refused solely because an operator paused routing.
    /// </summary>
    private static async Task WriteRoutingDisabledResponseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            error = new
            {
                message = "Routing is currently disabled.",
                type = "routing_disabled",
                code = "503"
            }
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }

    /// <summary>
    /// Writes a client-facing 402 error envelope when every candidate provider for a request is over its
    /// monthly budget. Matches <see cref="WriteModelNotFoundResponseAsync"/>'s OpenAI-shaped envelope but as
    /// a 402 (Payment Required): the request was valid and the model known - it was refused because the
    /// operator's spend cap is exhausted, which is a distinct, retriable-next-month condition, not a
    /// malformed request or an upstream outage.
    /// </summary>
    private static async Task WriteBudgetExhaustedResponseAsync(HttpContext context, string requestedModel)
    {
        context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            error = new
            {
                message = $"model '{requestedModel}' and all its fallbacks are over their configured monthly budget.",
                type = "budget_exhausted",
                param = "model",
                code = "402"
            }
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }
}

