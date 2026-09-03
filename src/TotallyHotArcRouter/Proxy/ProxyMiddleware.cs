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
using TotallyHot.ArcRouter.Proxy.Management;
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
/// One entry in <see cref="ProxyMiddleware.CandidateGates"/>'s ordered per-candidate pre-flight
/// sequence: a named predicate plus the exact logging its <c>InvokeCoreAsync</c> call site performed
/// before this became data-driven, so re-expressing the sequence as a list changes nothing observable
/// - same conditions, same log templates and arguments, same order.
/// </summary>
/// <param name="Name">Short identifier for the gate, used only for readability at the call site (e.g. in a debugger or future diagnostics) - never logged or compared against.</param>
/// <param name="Predicate">Returns <see langword="true"/> when this gate blocks <paramref name="Predicate"/>'s candidate. Takes the owning <see cref="ProxyMiddleware"/> explicitly (an open instance delegate) because the six underlying checks are instance methods; the <see cref="CircuitBreakerTargetKey"/> parameter is unused by the gates that do not need it.</param>
/// <param name="LogBlocked">Emits the gate's specific "why this candidate was skipped" log entry. Invoked only when <see cref="Predicate"/> returned <see langword="true"/>.</param>
internal readonly record struct GateCheck(
    string Name,
    Func<ProxyMiddleware, ResolvedModelRoute, CircuitBreakerTargetKey, bool> Predicate,
    Action<ProxyMiddleware, ResolvedModelRoute> LogBlocked);

/// <summary>
/// Middleware for handling and forwarding proxy requests.
/// </summary>
public class ProxyMiddleware : IMiddleware, IDisposable
{
    /// <summary>
    /// The per-candidate pre-flight gate sequence <see cref="InvokeCoreAsync"/> walks, in this exact
    /// order, for every candidate before attempting it. The order is load-bearing, not incidental: gate
    /// (4), the read-only circuit-breaker pre-check, MUST run before gates (5) and (6) - each of which
    /// can mutate breaker state and claim a target's single half-open probe slot. If a mutating gate
    /// claimed that slot and a *later* gate then rejected the candidate anyway, the probe would never be
    /// resolved via <c>RecordSuccess</c>/<c>RecordFailure</c> (this candidate is never attempted) and
    /// would stay "in flight" forever, permanently bypassing that target. Running the deterministic,
    /// non-mutating pre-check first filters out the already-OPEN case before either mutating gate is
    /// even reached. Gates (1)-(3) carry no such ordering constraint among themselves; they keep their
    /// historical order only to avoid an unnecessary diff. Expressing the sequence as data (rather than
    /// as inline <c>if</c> statements) makes this invariant explicit in one place instead of only in a
    /// comment, so a future reordering has to touch this list deliberately rather than silently
    /// violating it.
    /// </summary>
    private static readonly IReadOnlyList<GateCheck> CandidateGates =
    [
        new(
            "budget",
            static (middleware, route, _) => middleware.IsBudgetGateBlocked(route),
            static (middleware, route) => middleware._logger.LogInformation(
                "Skipping provider {Provider} for model {Model}: monthly budget exhausted.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            "provider-disabled",
            static (middleware, route, _) => middleware.IsProviderDisabledGateBlocked(route),
            static (middleware, route) => middleware._logger.LogInformation(
                "Bypassing provider {Provider} for model {Model}: provider is stopped.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            "model-disabled",
            static (middleware, route, _) => middleware.IsModelDisabledGateBlocked(route),
            static (middleware, route) => middleware._logger.LogInformation(
                "Bypassing model {Model}: stopped or not currently reported by its provider's endpoint.",
                LogRedaction.Sanitize(route.ModelName))),
        new(
            "circuit-open-precheck",
            static (middleware, route, circuitTarget) => middleware.IsCircuitOpenPreCheckGateBlocked(route, circuitTarget),
            static (middleware, route) => middleware._logger.LogInformation(
                "Bypassing provider {Provider} for model {Model}: circuit breaker is open.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            "circuit-bypass-target",
            static (middleware, _, circuitTarget) => middleware.IsCircuitBypassGateBlocked(circuitTarget),
            static (middleware, route) => middleware._logger.LogInformation(
                "Bypassing provider {Provider} for model {Model}: circuit breaker is open.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            "circuit-bypass-provider",
            static (middleware, route, _) => middleware.IsCircuitBypassProviderGateBlocked(route),
            static (middleware, route) => middleware._logger.LogInformation(
                "Bypassing provider {Provider} for model {Model}: provider-wide circuit breaker is open.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
    ];

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

    // Cap on how much of the response body telemetry captures for usage parsing (see CopyAndCaptureAsync).
    // Real chat/completion responses are almost always well under this; a response that exceeds it just
    // means usage parsing has less to work with (a truncated/partial buffer that the usage parsers already
    // handle gracefully by finding nothing), never a failure of the actual client-facing forward, which is
    // unaffected by this cap - every byte is still copied to the client regardless.
    internal const int MaxCapturedResponseBytes = 4 * 1024 * 1024;

    private readonly ILogger<ProxyMiddleware> _logger;
    private readonly HttpClient _httpClient;
    private readonly RequestInterceptor _interceptor;
    private readonly IReadOnlyDictionary<string, IPayloadTranslator> _translators;
    private readonly IBedrockRuntimeClientFactory _bedrockClientFactory;
    private readonly PriceCatalog.IBudgetEnforcer? _budgetStore;
    private readonly IRateLimitHeaderCapture _rateLimitCapture;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IProviderInteractionStatusStore? _interactionStatusStore;
    private readonly ToolCallNormalizerFactory _toolCallNormalizerFactory;
    private readonly InFlightRequestGauge? _inFlightGauge;
    private readonly Router.IRoutingGate? _routingGate;

    // Answers the three self-contained local endpoints (/v1/models, /api/tags, /api/show) - see
    // LocalEndpointResponder's own remarks for why this was the first cut out of this class.
    private readonly LocalEndpointResponder _localEndpointResponder;

    // Resolves session/turn identity, extracts usage/cost, and publishes telemetry for a served request -
    // see RequestTelemetryPublisher's own remarks for why this was the second cut out of this class.
    private readonly RequestTelemetryPublisher _requestTelemetryPublisher;

    // Invokes Bedrock directly via its SDK, mirroring the HTTP forwarding path - see
    // BedrockInvocationHandler's own remarks for why this was the third cut out of this class.
    private readonly BedrockInvocationHandler _bedrockInvocationHandler;

    // True only when no factory was supplied and this instance built its own fallback - in that case
    // ProxyMiddleware is the sole owner of that factory's lifetime and must dispose it (it caches AWS SDK
    // clients and implements IDisposable; see BedrockRuntimeClientFactory's remarks). When a factory is
    // supplied (the real app's DI-registered singleton), its lifetime belongs to whoever registered it -
    // disposing it here would pull it out from under other consumers of the same DI-owned instance.
    private readonly bool _ownsBedrockClientFactory;

    private static readonly IReadOnlyDictionary<string, IPayloadTranslator> NoTranslators =
        new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Response header carrying the client's literal requested model (docs/router/orchestrator-live-path-plan.md §M2.2).</summary>
    internal const string RequestedModelHeaderName = "X-ArcRouter-Requested-Model";

    /// <summary>Response header carrying the model that actually served the request.</summary>
    internal const string RoutedModelHeaderName = "X-ArcRouter-Routed-Model";

    /// <summary>Response header carrying the <see cref="RoutingSubstitutionReason"/> for why the two headers above differ, if they do.</summary>
    internal const string SubstitutionReasonHeaderName = "X-ArcRouter-Substitution-Reason";

    /// <summary>
    /// The result of capturing a translated response for telemetry: the OpenAI-shaped bytes actually sent
    /// to the client, and - only for a provider <see cref="UsageExtractor.SupportsNativeShape"/> recognizes
    /// - a second, capped copy of the pre-translation native bytes, immune to translation lossiness
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4). <see cref="NativeBytes"/> is
    /// <see langword="null"/> for a pass-through (no translator ran, so the client bytes already are native)
    /// or an unsupported translated provider (e.g. Gemini). <see cref="TailScanner"/> retained a trailing
    /// window over the client-shape stream, independent of <see cref="ClientShapeBytes"/>'s head cap -
    /// consulted by <see cref="RequestTelemetryPublisher.PublishAsync"/> only when usage extraction fails against both the
    /// head-capped and native captures (§5.11).
    /// </summary>
    private readonly record struct CapturedResponse(byte[] ClientShapeBytes, byte[]? NativeBytes, IncrementalUsageScanner? TailScanner);

    /// <summary>
    /// Owns the "cap the captured bytes, and once the cap is exceeded, lazily allocate an
    /// <see cref="IncrementalUsageScanner"/> to keep scanning a tail window" accounting rule that
    /// <see cref="TranslateAndCaptureStreamAsync"/> and <see cref="CopyAndCaptureAsync"/> each drove
    /// independently (streamed and raw copy-through call shapes) before this type existed - both enforce
    /// the same rule, just triggered from different loops. <see cref="TranslateAndCaptureBufferedAsync"/>
    /// deliberately does not use this type - see its remarks. <see cref="_trackTail"/> disables the tail
    /// scanner for a capture that never consults one (the secondary native-bytes copy in
    /// <see cref="TranslateAndCaptureStreamAsync"/>), so a capacity-exceeding chunk there doesn't pay for a
    /// scanner allocation nothing will ever read.
    /// </summary>
    private sealed class ResponseCaptureAccumulator : IDisposable
    {
        private readonly MemoryStream _capture = new();
        private readonly int _captureCap;
        private readonly bool _trackTail;
        private IncrementalUsageScanner? _tailScanner;

        public ResponseCaptureAccumulator(int captureCap, bool trackTail = true)
        {
            _captureCap = captureCap;
            _trackTail = trackTail;
        }

        /// <summary>The tail scanner allocated once a chunk first exceeded the cap, or <see langword="null"/> if none has (yet), or if this accumulator does not track one.</summary>
        public IncrementalUsageScanner? TailScanner => _tailScanner;

        /// <summary>
        /// Writes up to the remaining cap of <paramref name="chunk"/> into the capture buffer, and - when
        /// tracking a tail scanner - appends <paramref name="chunk"/> to it in full once the cap has been
        /// exceeded, so the very chunk that crosses the cap boundary is still captured in full there, not
        /// just the chunks after it.
        /// </summary>
        public async Task AddAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (chunk.IsEmpty)
            {
                return;
            }

            var remainingCapacity = _captureCap - (int)_capture.Length;
            if (remainingCapacity > 0)
            {
                await _capture.WriteAsync(chunk[..Math.Min(chunk.Length, remainingCapacity)], cancellationToken);
            }

            if (_trackTail && remainingCapacity < chunk.Length)
            {
                _tailScanner ??= new IncrementalUsageScanner();
                _tailScanner.Append(chunk.Span);
            }
        }

        /// <summary>Returns the bytes captured so far, up to the configured cap.</summary>
        public byte[] ToArray() => _capture.ToArray();

        /// <inheritdoc />
        public void Dispose() => _capture.Dispose();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyMiddleware"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="interceptor">Request/response interceptor.</param>
    /// <param name="httpClient">Optional HTTP client used for forwarding requests.</param>
    /// <param name="dependencies">
    /// The optional collaborators this instance can be given, carried as one named object - see
    /// <see cref="ProxyMiddlewareDependencies"/>'s own remarks for why. <see langword="null"/> (the
    /// default) is equivalent to an empty <see cref="ProxyMiddlewareDependencies"/>: every optional
    /// feature falls back to its documented behaviorally-inert default, exactly as before this bag existed.
    /// </param>
    public ProxyMiddleware(
        ILogger<ProxyMiddleware> logger,
        RequestInterceptor interceptor,
        HttpClient? httpClient = null,
        ProxyMiddlewareDependencies? dependencies = null)
    {
        _logger = logger;
        _interceptor = interceptor;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        _translators = dependencies?.Translators ?? NoTranslators;
        _budgetStore = dependencies?.BudgetStore;
        _rateLimitCapture = dependencies?.RateLimitCapture ?? NullRateLimitHeaderCapture.Instance;
        _circuitBreaker = dependencies?.CircuitBreaker ?? new CircuitBreaker();
        _toolCallNormalizerFactory = dependencies?.ToolCallNormalizerFactory ?? new ToolCallNormalizerFactory();
        _inFlightGauge = dependencies?.InFlightGauge;
        _routingGate = dependencies?.RoutingGate;
        _localEndpointResponder = new LocalEndpointResponder(logger, interceptor, dependencies?.CapabilityStore, dependencies?.ContextWindowStore);
        _interactionStatusStore = dependencies?.InteractionStatusStore;
        _requestTelemetryPublisher = new RequestTelemetryPublisher(
            logger,
            dependencies?.SessionIdResolver ?? new SessionIdResolver(),
            dependencies?.ContinuityMatcher ?? new MessageHistoryContinuityMatcher(),
            dependencies?.TurnTracker ?? new ConversationTurnTracker(),
            dependencies?.UsageExtractor ?? new UsageExtractor(),
            dependencies?.ResponseTextExtractor ?? new ResponseTextExtractor(),
            dependencies?.TelemetryPublisher ?? new TelemetryPublisher(new TelemetryBroadcaster()),
            dependencies?.QualityIngress,
            dependencies?.SpendTracker ?? NullSpendTracker.Instance,
            dependencies?.PriceLookup,
            dependencies?.BudgetStore,
            dependencies?.UsageLedger,
            dependencies?.PendingTaskEmbeddingCache,
            dependencies?.PendingRequestCostCache,
            dependencies?.PendingRequestProvenanceCache,
            dependencies?.PendingResponseTextCache,
            dependencies?.TranscriptStore,
            dependencies?.RoutingOptionsMonitor,
            dependencies?.JudgeOptionsMonitor,
            dependencies?.RoutingOptions?.Value.SelfHostedRouterPricePerMillionTokens ?? new Models.RoutingOptions().SelfHostedRouterPricePerMillionTokens);

        if (dependencies?.BedrockClientFactory is null)
        {
            _bedrockClientFactory = new BedrockRuntimeClientFactory();
            _ownsBedrockClientFactory = true;
        }
        else
        {
            _bedrockClientFactory = dependencies.BedrockClientFactory;
        }

        _bedrockInvocationHandler = new BedrockInvocationHandler(logger, _bedrockClientFactory, _circuitBreaker, _requestTelemetryPublisher);
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
        _logger.LogInformation("Proxy middleware caught request to {Path}", LogRedaction.Sanitize(context.Request.Path.ToString()));

        if (LocalEndpointResponder.IsModelsListRequest(context.Request))
        {
            await _localEndpointResponder.WriteModelsListResponseAsync(context);
            return;
        }

        if (LocalEndpointResponder.IsOllamaTagsRequest(context.Request))
        {
            await _localEndpointResponder.WriteOllamaTagsResponseAsync(context);
            return;
        }

        if (LocalEndpointResponder.IsOllamaShowRequest(context.Request))
        {
            await _localEndpointResponder.WriteOllamaShowResponseAsync(context);
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

        // docs/adr/0004-.../0005-...: an explicit selection whose target or provider is already
        // circuit-open never reaches the candidate loop at all - RequestInterceptor deliberately left it
        // unsubstituted (so candidates[0] still reports the client's real choice for telemetry), and the
        // truthful message it already resolved is written directly here instead of attempting - or
        // silently substituting away from - a target everyone already knows is untrustworthy.
        if (resolution.ExplicitCircuitTripBlockMessage is { } blockedMessage)
        {
            await WriteCircuitTripBlockedResponseAsync(context, blockedMessage);
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
                LogRedaction.Sanitize(requestedModelName));
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
        // The candidate pre-flight gate sequence below is CandidateGates, walked in order: (1) budget
        // breach, (2) provider-enabled, (3) model-enabled, (4) circuit-breaker read-only pre-check, (5)
        // circuit ShouldBypass (target-level), (6) circuit ShouldBypassProvider (provider-wide). The
        // order of (4) before (5)/(6) is load-bearing, not incidental: ShouldBypass/ShouldBypassProvider
        // can each claim a target's single half-open probe slot, and running the read-only pre-check
        // first filters out the deterministic already-OPEN case before either mutating check is even
        // reached - see CandidateGates' own documentation, and gate (4)'s predicate below, for why a
        // claimed-then-abandoned probe would otherwise strand that target forever. Gates (1)-(3) have no
        // such ordering constraint among themselves but are kept in their historical order to avoid a
        // behavior-neutral but unnecessary diff.
        for (var i = 0; i < candidates.Count; i++)
        {
            var route = candidates[i].Route;

            // circuitTarget is computed up front (rather than only just before gate (4), as it used to
            // be) purely so every gate can share one uniform (route, circuitTarget) signature below - it
            // is a pure struct construction with no side effects, so hoisting it earlier changes nothing
            // observable. See CandidateGates' own documentation for why gates (1)-(6) must run in this
            // exact order.
            var circuitTarget = CircuitBreakerTargetKey.FromRoute(route);

            var candidateGateBlocked = false;
            foreach (var gate in CandidateGates)
            {
                if (gate.Predicate(this, route, circuitTarget))
                {
                    gate.LogBlocked(this, route);
                    candidateGateBlocked = true;
                    break;
                }
            }

            if (candidateGateBlocked)
            {
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
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName),
                LogRedaction.Sanitize(route.ProviderModelId),
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
            // longer unconditionally terminal: BedrockInvocationHandler.InvokeAsync returns true once it has actually written
            // a response to the client (success, or a failure with no eligible next candidate) - that's
            // this request's final outcome, so the loop stops. It returns false when the SDK call failed
            // *before* writing anything (same "nothing committed yet" invariant the HTTP path below relies
            // on) and a next candidate is worth trying, in which case the loop falls through to `continue`
            // exactly like an HTTP outage does.
            if (translator is IBedrockPayloadTranslator bedrockTranslator)
            {
                if (await _bedrockInvocationHandler.InvokeAsync(context, route, bedrockTranslator, rewrittenBody, requestedModelName, isFallback, hasNextCandidate, nextProviderDiffers, resolution.TaskEmbedding, resolution.RouterTokens, resolution.SubstitutionReason, resolution.IsExploratory, resolution.Propensity, resolution.Classification, resolution.TaskText, resolution.DimBestModel))
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
                    _logger.LogWarning(ex, "Upstream provider {Provider} unreachable for model {Model}; failing over to the next backup.", LogRedaction.Sanitize(route.Provider), LogRedaction.Sanitize(route.ModelName));
                    continue;
                }

                _logger.LogWarning(ex, "Upstream provider {Provider} unreachable for model {Model}; no backup remains.", LogRedaction.Sanitize(route.Provider), LogRedaction.Sanitize(route.ModelName));
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
            // Anthropic 400s carry the same problem as Gemini's embedded-error case below (minus the
            // disguised-401 auth wrinkle): its native error shape (`{"type":"error","error":{...}}`) has none
            // of the fields TranslateResponse expects, so running it through the translator would mangle it
            // into a bogus empty completion instead of surfacing the real rejection reason - see
            // AnthropicPayloadTranslator.TryExtractEmbeddedError's own doc comment.
            byte[]? preReadErrorBody = null;
            string? embeddedErrorMessage = null;
            var isProviderAuthFailure = false;

            // Which statuses carry a decodable error envelope is the *translator's* judgement, not this
            // middleware's: asking IPayloadTranslator.HandlesEmbeddedErrorAt is what replaced a chain of
            // `translator is GeminiPayloadTranslator` / `is AnthropicPayloadTranslator` type tests calling
            // static extractors. That chain meant a newly registered translated provider was silently
            // un-classified - its embedded errors mangled by TranslateResponse into a bogus empty
            // completion - until someone remembered to extend this method. Now a provider that says
            // nothing gets the interface's safe default (no pre-read, body forwarded untouched), and a
            // provider that knows its own error shape opts in without this file changing at all.
            //
            // docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md: an
            // untranslated/passthrough provider (OpenAI-compatible, LM Studio, Ollama-native, etc.) has no
            // translator to ask, so its 400/429 rule stays here. Its error body is otherwise forwarded
            // byte-for-byte untouched, so reading it once for classification changes nothing about what the
            // client eventually receives; see the preReadErrorBody-is-not-null forwarding branch below.
            var shouldPreReadErrorBody = translator is not null
                ? translator.HandlesEmbeddedErrorAt(statusCode)
                : statusCode == StatusCodes.Status400BadRequest || statusCode == 429;

            if (shouldPreReadErrorBody)
            {
                preReadErrorBody = await responseMessage.Content.ReadAsByteArrayAsync(context.RequestAborted);
                if (translator is not null && translator.TryExtractEmbeddedError(preReadErrorBody, out var embedded))
                {
                    embeddedErrorMessage = embedded.Message;
                    isProviderAuthFailure = embedded.IsAuthFailure;
                }
            }

            // "What does this response mean?" - the circuit-breaker signal it carries, and whether the
            // request should fail over - belongs to UpstreamFailureClassifier, and is deliberately pure.
            // The ADR-0004/0005 failover rules are where regressions on this path land, and they are only
            // cheap to test exhaustively if evaluating them needs no HttpContext, no circuit breaker, and no
            // upstream at all. ApplyHealthSignal below is the half that logs and mutates.
            var verdict = UpstreamFailureClassifier.Classify(
                statusCode,
                preReadErrorBody,
                embeddedErrorMessage,
                isProviderAuthFailure,
                nextProviderDiffers,
                isExplicitPrimary: !isFallback && resolution.SubstitutionReason == RoutingSubstitutionReason.None);

            ApplyHealthSignal(verdict, route, circuitTarget, statusCode);

            var shouldRetryThisCandidate = verdict.ShouldRetry;

            if (hasNextCandidate && shouldRetryThisCandidate)
            {
                _logger.LogWarning("Upstream provider {Provider} returned {Status} for model {Model}; failing over to the next backup.", LogRedaction.Sanitize(route.Provider), statusCode, LogRedaction.Sanitize(route.ModelName));
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
                context.Response.Headers[SubstitutionReasonHeaderName] = RequestTelemetryPublisher.ResolveSubstitutionReason(isFallback, resolution.SubstitutionReason).ToString();

                byte[] capturedResponseBytes;
                byte[]? nativeResponseBytes = null;
                IncrementalUsageScanner? tailScanner = null;
                if (preReadErrorBody is not null && embeddedErrorMessage is not null)
                {
                    // A Gemini or Anthropic 400 that reached here (for Gemini: either it wasn't the
                    // UNAUTHENTICATED case, or it was but no differing-provider candidate remained to fail
                    // over to) whose body actually contained an embedded error object (TryExtractEmbeddedError
                    // succeeded). The body was already read above to make that determination; running it
                    // through TranslateResponse/the stream translator now would mangle it into a bogus empty
                    // completion or (for the SSE shape) silently swallow it - see TryExtractEmbeddedError's
                    // caller - so a clean OpenAI-shaped error is written directly instead, preserving whatever
                    // message the provider actually sent.
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.Remove("Content-Length");
                    var errorPayload = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        error = new
                        {
                            message = embeddedErrorMessage,
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
                    // A Gemini or Anthropic 400 whose body didn't contain a recognizable embedded error object
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

                _logger.LogDebug(
                    "[INTERCEPTOR] Intercepted agent response message: {ResponseBody}",
                    LogRedaction.Truncate(LogRedaction.Sanitize(Encoding.UTF8.GetString(capturedResponseBytes))));

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
                    await _requestTelemetryPublisher.PublishAsync(context, route, requestedModelName, isFallback, telemetryShapeProvider, rewrittenBody, capturedResponseBytes, nativeResponseBytes, isStreaming, latencyToHeadersMs, totalDurationMs, statusCode, context.RequestAborted, responseMessage.Headers, tailScanner, resolution.TaskEmbedding, resolution.RouterTokens, resolution.SubstitutionReason, resolution.IsExploratory, resolution.Propensity, resolution.Classification, resolution.TaskText, resolution.DimBestModel);
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
                    LogRedaction.Sanitize(requestedModelName));
                await WriteBudgetExhaustedResponseAsync(context, requestedModelName);
            }
            else if (candidates.All(c => !_interceptor.IsProviderEnabled(c.Route.Provider) || !_interceptor.IsModelEnabled(c.Route.ModelName)))
            {
                _logger.LogWarning(
                    "All candidate routes for model {Model} are stopped (provider stopped, model stopped, or not currently reported by its provider's endpoint); rejecting with 503.",
                    LogRedaction.Sanitize(requestedModelName));
                await WriteUpstreamErrorResponseAsync(
                    context,
                    "All configured routes for this model are currently stopped.",
                    StatusCodes.Status503ServiceUnavailable);
            }
            else
            {
                _logger.LogWarning(
                    "All candidate routes for model {Model} are currently circuit-broken; rejecting with 502.",
                    LogRedaction.Sanitize(requestedModelName));
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
    internal static bool IsStreamAbort(Exception ex) => ex is OperationCanceledException or IOException or SocketException;

    /// <summary>
    /// Applies an <see cref="UpstreamFailureClassifier"/> verdict: records the circuit-breaker signal, logs
    /// the operator-actionable reason, and updates the Providers tab's live-traffic state. The effectful
    /// half of what used to be one ~130-line if/else chain inside <see cref="InvokeCoreAsync"/> - the
    /// decisions themselves are in <see cref="UpstreamFailureClassifier.Classify"/>, which is pure and
    /// separately tested.
    ///
    /// <para>
    /// A provider-wide cause trips every model on the provider at once
    /// (<see cref="ICircuitBreaker.RecordProviderFailure"/>) rather than just this target, because a bad
    /// credential, a permission-scope problem, an edge gateway block, or an empty account would break every
    /// model on that provider identically. A per-target outage (5xx/429/404) trips only this target, since
    /// it says nothing about the provider's other models.
    /// </para>
    /// </summary>
    /// <param name="verdict">The classification of the upstream response.</param>
    /// <param name="route">The candidate that produced the response - supplies the provider and model names for logging.</param>
    /// <param name="circuitTarget">The per-target circuit-breaker key for <paramref name="route"/>.</param>
    /// <param name="statusCode">The upstream status code, logged as-is on the failover path.</param>
    private void ApplyHealthSignal(
        UpstreamFailureVerdict verdict,
        ResolvedModelRoute route,
        CircuitBreakerTargetKey circuitTarget,
        int statusCode)
    {
        switch (verdict.HealthSignal)
        {
            case ProviderHealthSignal.ProviderWideOutage:
                LogProviderWideOutage(verdict.ProviderWideCause, route, statusCode);
                _circuitBreaker.RecordProviderFailure(route.Provider);

                if (verdict.ProviderWideCause is ProviderWideOutageCause.OutOfCredits)
                {
                    _interactionStatusStore?.RecordLiveTrafficFailure(
                        route.Provider,
                        ProviderInteractionKind.OutOfCredits,
                        verdict.OutOfCreditsMessage);
                }

                break;

            case ProviderHealthSignal.TargetOutage:
                _circuitBreaker.RecordFailure(circuitTarget);
                break;

            default:
                _circuitBreaker.RecordSuccess(circuitTarget);

                // docs/adr/0004-...: gated strictly on an actual 2xx, not the broader "not an outage" bucket
                // this branch otherwise covers (which also includes a plain non-out-of-credits 400/422) - a
                // malformed request succeeding at the transport layer is not evidence the provider "works"
                // in the billing sense LiveTraffic tracks, so it must not clear a live out-of-credits warning.
                if (verdict.IsSuccessStatus)
                {
                    _interactionStatusStore?.RecordLiveTrafficSuccess(route.Provider, "Live traffic");
                }

                break;
        }
    }

    /// <summary>
    /// Logs the specific reason a response was judged provider-wide. Split out so
    /// <see cref="ApplyHealthSignal"/> reads as one decision rather than five, and so each cause keeps the
    /// distinct, operator-actionable message it had when this was an if/else chain - the remedies differ
    /// (rotate a key, widen a permission scope, take it up with the provider's gateway, top up credits), so
    /// a single merged message would be worse than the five it replaces.
    /// </summary>
    /// <param name="cause">Why the response was judged provider-wide.</param>
    /// <param name="route">The candidate that produced the response.</param>
    /// <param name="statusCode">The upstream status code, used only by the out-of-credits message.</param>
    private void LogProviderWideOutage(ProviderWideOutageCause cause, ResolvedModelRoute route, int statusCode)
    {
        var provider = LogRedaction.Sanitize(route.Provider);
        var model = LogRedaction.Sanitize(route.ModelName);

        switch (cause)
        {
            case ProviderWideOutageCause.Unauthorized:
                _logger.LogError(
                    "Upstream provider {Provider} returned 401 Unauthorized for model {Model}; treating as a provider-wide outage (likely an invalid or expired credential) and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.EmbeddedCredentialError:
                _logger.LogError(
                    "Upstream provider {Provider} returned an embedded credential error for model {Model} on a non-401 status (Gemini, for example, reports an invalid API key as a 400); treating as a provider-wide outage and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.Forbidden:
                _logger.LogError(
                    "Upstream provider {Provider} returned 403 Forbidden for model {Model}; treating as a provider-wide outage (likely a permission or API-key-scope problem) and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.MethodNotAllowed:
                // Seen in production as an Alibaba Cloud WAF block page (an HTML "access blocked" response,
                // not a real API error) served instead of reaching the model API at all - a gateway-level
                // rejection of this request, not evidence about the request's actual validity.
                _logger.LogError(
                    "Upstream provider {Provider} returned 405 Method Not Allowed for model {Model}; treating as a provider-wide gateway/WAF block and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.OutOfCredits:
                _logger.LogError(
                    "Upstream provider {Provider} is out of credits for model {Model} (status {Status}); treating as a provider-wide outage and bypassing every model on this provider until it recovers.",
                    provider,
                    model,
                    statusCode);
                break;
        }
    }

    /// <summary>Gate (1) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: <see langword="true"/> when <paramref name="route"/>'s provider is over its monthly budget.</summary>
    private bool IsBudgetGateBlocked(ResolvedModelRoute route) =>
        _budgetStore is not null && _budgetStore.IsBreached(route.Provider);

    /// <summary>Gate (2) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: <see langword="true"/> when <paramref name="route"/>'s provider is stopped (Governance &gt; Providers).</summary>
    private bool IsProviderDisabledGateBlocked(ResolvedModelRoute route) =>
        !_interceptor.IsProviderEnabled(route.Provider);

    /// <summary>Gate (3) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: <see langword="true"/> when <paramref name="route"/>'s model is stopped or no longer reported by its provider's endpoint.</summary>
    private bool IsModelDisabledGateBlocked(ResolvedModelRoute route) =>
        !_interceptor.IsModelEnabled(route.ModelName);

    /// <summary>
    /// Gate (4) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: the read-only circuit
    /// pre-check that MUST run before gates (5)/(6) - see the ordering note above the loop in
    /// <see cref="InvokeCoreAsync"/> for why.
    /// </summary>
    private bool IsCircuitOpenPreCheckGateBlocked(ResolvedModelRoute route, CircuitBreakerTargetKey circuitTarget) =>
        _circuitBreaker.IsOpen(circuitTarget) || _circuitBreaker.IsProviderOpen(route.Provider);

    /// <summary>Gate (5) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: the target-level circuit breaker, which may claim a half-open probe slot - see gate (4)'s remarks.</summary>
    private bool IsCircuitBypassGateBlocked(CircuitBreakerTargetKey circuitTarget) =>
        _circuitBreaker.ShouldBypass(circuitTarget);

    /// <summary>Gate (6) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: the provider-wide circuit breaker, which may claim a half-open probe slot - see gate (4)'s remarks.</summary>
    private bool IsCircuitBypassProviderGateBlocked(ResolvedModelRoute route) =>
        _circuitBreaker.ShouldBypassProvider(route.Provider);

    /// <summary>Writes a client-facing error envelope for a failed upstream call (a Bedrock SDK failure, or an exhausted transport-outage cascade), matching <see cref="WriteModelNotFoundResponseAsync"/>'s shape. Defaults to 502 (the request was valid; the upstream call failed) rather than 400 (a malformed/unknown-model client request); <paramref name="statusCode"/> lets a caller override this - e.g. 401 for a Bedrock credential-resolution failure treated like the HTTP path's 401 handling. Callers pass a client-safe <paramref name="errorMessage"/> - never a raw transport-exception message, which can leak infrastructure detail.</summary>
    internal static async Task WriteUpstreamErrorResponseAsync(HttpContext context, string errorMessage, int statusCode = StatusCodes.Status502BadGateway)
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
    /// Determines whether the (OpenAI-shaped) request body asked for a streaming response, i.e. its
    /// top-level <c>stream</c> field is <see langword="true"/>. Used only for translated providers, to
    /// pick the streaming vs non-streaming upstream URL and response path.
    /// </summary>
    internal static bool IsStreamingRequest(byte[] requestBody)
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

            // Deliberately not routed through ResponseCaptureAccumulator: unlike the two loop-based capture
            // methods below, this is a single, already-fully-materialized buffer, and the common case (a
            // response that fits within captureCap) can return the translated array directly instead of
            // paying for a MemoryStream copy the accumulator's chunk-oriented API would otherwise force.
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
        using var capture = new ResponseCaptureAccumulator(captureCap);
        using var nativeCapture = captureNativeBytes ? new ResponseCaptureAccumulator(captureCap, trackTail: false) : null;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        async Task EmitAsync(byte[] translated)
        {
            if (translated.Length == 0)
            {
                return;
            }

            await destination.WriteAsync(translated, cancellationToken);
            await destination.FlushAsync(cancellationToken);

            await capture.AddAsync(translated, cancellationToken);
        }

        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                if (nativeCapture is not null)
                {
                    await nativeCapture.AddAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
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

        return new CapturedResponse(capture.ToArray(), nativeCapture?.ToArray(), capture.TailScanner);
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
        using var capture = new ResponseCaptureAccumulator(captureCap);
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

                await capture.AddAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
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

        return (capture.ToArray(), capture.TailScanner);
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
    /// Writes an OpenAI-shaped <c>{"error": {...}}</c> envelope: the shared shape every client-facing error
    /// response in this class uses, differing only by status code, <paramref name="type"/>, message, and an
    /// optional <paramref name="param"/> (omitted from the JSON entirely when <see langword="null"/>, via
    /// <see cref="ErrorDetail.Param"/>'s <c>WhenWritingNull</c> condition). <paramref name="statusCode"/> is
    /// echoed back as the envelope's string <c>code</c> field, matching every existing call site.
    /// </summary>
    private static async Task WriteErrorResponseAsync(HttpContext context, int statusCode, string type, string message, string? param = null)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = new ErrorEnvelope(new ErrorDetail(message, type, param, statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }

    /// <summary>The top-level <c>{"error": {...}}</c> envelope <see cref="WriteErrorResponseAsync"/> writes.</summary>
    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] ErrorDetail Error);

    /// <summary>The body of an OpenAI-shaped error envelope written by <see cref="WriteErrorResponseAsync"/>.</summary>
    private sealed record ErrorDetail(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("param")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Param,
        [property: JsonPropertyName("code")] string Code);

    /// <summary>Writes a 400 response in an OpenAI-shaped error envelope for a request whose model could not be resolved.</summary>
    private static Task WriteModelNotFoundResponseAsync(HttpContext context, string errorMessage) =>
        WriteErrorResponseAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", errorMessage, param: "model");

    /// <summary>
    /// Writes a 503 response in an OpenAI-shaped error envelope when the GUI system tray's kill switch
    /// (<see cref="Router.IRoutingGate"/>) has routing disabled. Matches
    /// <see cref="WriteModelNotFoundResponseAsync"/>'s envelope shape but as 503 (Service Unavailable): the
    /// request itself may be perfectly valid, it was refused solely because an operator paused routing.
    /// </summary>
    private static Task WriteRoutingDisabledResponseAsync(HttpContext context) =>
        WriteErrorResponseAsync(context, StatusCodes.Status503ServiceUnavailable, "routing_disabled", "Routing is currently disabled.");

    /// <summary>
    /// Writes a client-facing 402 error envelope when every candidate provider for a request is over its
    /// monthly budget. Matches <see cref="WriteModelNotFoundResponseAsync"/>'s OpenAI-shaped envelope but as
    /// a 402 (Payment Required): the request was valid and the model known - it was refused because the
    /// operator's spend cap is exhausted, which is a distinct, retriable-next-month condition, not a
    /// malformed request or an upstream outage.
    /// </summary>
    private static Task WriteBudgetExhaustedResponseAsync(HttpContext context, string requestedModel) =>
        WriteErrorResponseAsync(
            context,
            StatusCodes.Status402PaymentRequired,
            "budget_exhausted",
            $"model '{requestedModel}' and all its fallbacks are over their configured monthly budget.",
            param: "model");

    /// <summary>
    /// Writes a client-facing 503 error envelope for an explicit selection whose target or provider is
    /// already circuit-open from an earlier request (docs/adr/0004-surface-out-of-credits-provider-
    /// failures-on-the-providers-tab.md, docs/adr/0005-protect-explicit-provider-selections-from-silent-
    /// substitution-on-any-circuit-trip.md). Unlike <see cref="WriteBudgetExhaustedResponseAsync"/>'s 402
    /// (a hard operator-configured cap) this is 503 (Service Unavailable): the client's own selection was
    /// valid, the router simply already knows this specific target or provider isn't answering right now
    /// and never made a network call to find out again.
    /// </summary>
    private static Task WriteCircuitTripBlockedResponseAsync(HttpContext context, string message) =>
        WriteErrorResponseAsync(context, StatusCodes.Status503ServiceUnavailable, "invalid_request_error", message);
}

