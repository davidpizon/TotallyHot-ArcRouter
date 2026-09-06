using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// One entry in <see cref="ProxyMiddleware.CandidateGates"/>'s ordered per-candidate pre-flight
/// sequence: a named predicate plus the exact logging its <c>InvokeCoreAsync</c> call site performed
/// before this became data-driven, so re-expressing the sequence as a list changes nothing observable
/// - same conditions, same log templates and arguments, same order.
/// </summary>
/// <param name="Name">
/// Short identifier for the gate, used only for readability at the call site (e.g. in a debugger or
/// future diagnostics) - never logged or compared against.
/// </param>
/// <param name="Predicate">
/// Returns <see langword="true"/> when this gate blocks <paramref name="Predicate"/>'s candidate.
/// Takes the owning <see cref="ProxyMiddleware"/> explicitly (an open instance delegate) because the six underlying checks
/// are instance methods; the <see cref="CircuitBreakerTargetKey"/> parameter is unused by the gates that do not need it.
/// </param>
/// <param name="LogBlocked">
/// Emits the gate's specific "why this candidate was skipped" log entry. Invoked only when
/// <see cref="Predicate"/> returned <see langword="true"/>.
/// </param>
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
    /// Response header carrying the client's literal requested model (docs/router/orchestrator-live-path-plan.md
    /// §M2.2).
    /// </summary>
    internal const string RequestedModelHeaderName = "X-ArcRouter-Requested-Model";

    /// <summary>Response header carrying the model that actually served the request.</summary>
    internal const string RoutedModelHeaderName = "X-ArcRouter-Routed-Model";

    /// <summary>
    /// Response header carrying the <see cref="RoutingSubstitutionReason"/> for why the two headers above differ, if
    /// they do.
    /// </summary>
    internal const string SubstitutionReasonHeaderName = "X-ArcRouter-Substitution-Reason";

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
            Name: "budget",
            Predicate: static (middleware, route, _) => middleware.IsBudgetGateBlocked(route),
            LogBlocked: static (middleware, route) => middleware._logger.LogInformation(
                message: "Skipping provider {Provider} for model {Model}: monthly budget exhausted.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            Name: "provider-disabled",
            Predicate: static (middleware, route, _) => middleware.IsProviderDisabledGateBlocked(route),
            LogBlocked: static (middleware, route) => middleware._logger.LogInformation(
                message: "Bypassing provider {Provider} for model {Model}: provider is stopped.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            Name: "model-disabled",
            Predicate: static (middleware, route, _) => middleware.IsModelDisabledGateBlocked(route),
            LogBlocked: static (middleware, route) => middleware._logger.LogInformation(
                message: "Bypassing model {Model}: stopped or not currently reported by its provider's endpoint.",
                LogRedaction.Sanitize(route.ModelName))),
        new(
            Name: "circuit-open-precheck",
            Predicate: static (middleware, route, circuitTarget) =>
                middleware.IsCircuitOpenPreCheckGateBlocked(route: route, circuitTarget: circuitTarget),
            LogBlocked: static (middleware, route) => middleware._logger.LogInformation(
                message: "Bypassing provider {Provider} for model {Model}: circuit breaker is open.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            Name: "circuit-bypass-target",
            Predicate: static (middleware, _, circuitTarget) => middleware.IsCircuitBypassGateBlocked(circuitTarget),
            LogBlocked: static (middleware, route) => middleware._logger.LogInformation(
                message: "Bypassing provider {Provider} for model {Model}: circuit breaker is open.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName))),
        new(
            Name: "circuit-bypass-provider",
            Predicate: static (middleware, route, _) => middleware.IsCircuitBypassProviderGateBlocked(route),
            LogBlocked: static (middleware, route) => middleware._logger.LogInformation(
                message: "Bypassing provider {Provider} for model {Model}: provider-wide circuit breaker is open.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(route.ModelName)))
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

    private static readonly IReadOnlyDictionary<string, IPayloadTranslator> NoTranslators =
        new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase);

    private readonly IBedrockRuntimeClientFactory _bedrockClientFactory;

    // Invokes Bedrock directly via its SDK, mirroring the HTTP forwarding path - see
    // BedrockInvocationHandler's own remarks for why this was the third cut out of this class.
    private readonly BedrockInvocationHandler _bedrockInvocationHandler;
    private readonly IBudgetEnforcer? _budgetStore;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly HttpClient _httpClient;
    private readonly InFlightRequestGauge? _inFlightGauge;
    private readonly IProviderInteractionStatusStore? _interactionStatusStore;
    private readonly RequestInterceptor _interceptor;

    // Answers the three self-contained local endpoints (/v1/models, /api/tags, /api/show) - see
    // LocalEndpointResponder's own remarks for why this was the first cut out of this class.
    private readonly LocalEndpointResponder _localEndpointResponder;

    private readonly ILogger<ProxyMiddleware> _logger;

    // True only when no factory was supplied and this instance built its own fallback - in that case
    // ProxyMiddleware is the sole owner of that factory's lifetime and must dispose it (it caches AWS SDK
    // clients and implements IDisposable; see BedrockRuntimeClientFactory's remarks). When a factory is
    // supplied (the real app's DI-registered singleton), its lifetime belongs to whoever registered it -
    // disposing it here would pull it out from under other consumers of the same DI-owned instance.
    private readonly bool _ownsBedrockClientFactory;

    // Same ownership rule as _ownsBedrockClientFactory, for the same reason: true only when no client was
    // supplied and this instance built its own. A supplied HttpClient belongs to whoever supplied it -
    // in the real app that is the DI container, and in tests it is usually a client wrapping a stub
    // handler the test still uses afterward - so disposing it here would pull it out from under its owner.
    private readonly bool _ownsHttpClient;
    private readonly IRateLimitHeaderCapture _rateLimitCapture;

    // Resolves session/turn identity, extracts usage/cost, and publishes telemetry for a served request -
    // see RequestTelemetryPublisher's own remarks for why this was the second cut out of this class.
    private readonly RequestTelemetryPublisher _requestTelemetryPublisher;
    private readonly IRoutingGate? _routingGate;
    private readonly ToolCallNormalizerFactory _toolCallNormalizerFactory;
    private readonly IReadOnlyDictionary<string, IPayloadTranslator> _translators;
    private readonly UpstreamResponseWriter _upstreamResponseWriter;

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
        _ownsHttpClient = httpClient is null;
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
        _localEndpointResponder = new LocalEndpointResponder(logger: logger, interceptor: interceptor,
            capabilityStore: dependencies?.CapabilityStore, contextWindowStore: dependencies?.ContextWindowStore);
        _upstreamResponseWriter = new UpstreamResponseWriter(logger);
        _interactionStatusStore = dependencies?.InteractionStatusStore;
        _requestTelemetryPublisher = new RequestTelemetryPublisher(
            logger: logger,
            sessionIdResolver: dependencies?.SessionIdResolver ?? new SessionIdResolver(),
            continuityMatcher: dependencies?.ContinuityMatcher ?? new MessageHistoryContinuityMatcher(),
            turnTracker: dependencies?.TurnTracker ?? new ConversationTurnTracker(),
            usageExtractor: dependencies?.UsageExtractor ?? new UsageExtractor(),
            responseTextExtractor: dependencies?.ResponseTextExtractor ?? new ResponseTextExtractor(),
            telemetryPublisher: dependencies?.TelemetryPublisher ?? new TelemetryPublisher(new TelemetryBroadcaster()),
            qualityIngress: dependencies?.QualityIngress,
            spendTracker: dependencies?.SpendTracker ?? NullSpendTracker.Instance,
            priceLookup: dependencies?.PriceLookup,
            budgetStore: dependencies?.BudgetStore,
            usageLedger: dependencies?.UsageLedger,
            pendingTaskEmbeddingCache: dependencies?.PendingTaskEmbeddingCache,
            pendingRequestCostCache: dependencies?.PendingRequestCostCache,
            pendingRequestProvenanceCache: dependencies?.PendingRequestProvenanceCache,
            pendingResponseTextCache: dependencies?.PendingResponseTextCache,
            pendingPromptCache: dependencies?.PendingPromptCache,
            transcriptStore: dependencies?.TranscriptStore,
            routingOptionsMonitor: dependencies?.RoutingOptionsMonitor,
            judgeOptionsMonitor: dependencies?.JudgeOptionsMonitor,
            selfHostedRouterPricePerMillionTokens: dependencies?.RoutingOptions?.Value
                .SelfHostedRouterPricePerMillionTokens ?? new RoutingOptions().SelfHostedRouterPricePerMillionTokens);

        if (dependencies?.BedrockClientFactory is null)
        {
            _bedrockClientFactory = new BedrockRuntimeClientFactory();
            _ownsBedrockClientFactory = true;
        }
        else
        {
            _bedrockClientFactory = dependencies.BedrockClientFactory;
        }

        _bedrockInvocationHandler = new BedrockInvocationHandler(logger: logger,
            bedrockClientFactory: _bedrockClientFactory, circuitBreaker: _circuitBreaker,
            requestTelemetryPublisher: _requestTelemetryPublisher);
    }

    /// <summary>
    /// Disposes the collaborators this instance created for itself: the fallback
    /// <see cref="BedrockRuntimeClientFactory"/> (see <see cref="_ownsBedrockClientFactory"/>) and the
    /// fallback <see cref="HttpClient"/> (see <see cref="_ownsHttpClient"/>). Each is a no-op when the
    /// corresponding dependency was supplied, since a supplied instance's lifetime belongs to its own
    /// owner - the DI container in the real app. <see cref="ProxyMiddleware"/> is itself a DI-registered
    /// singleton, so the container invokes this at shutdown the same way it would for any other
    /// disposable singleton.
    /// </summary>
    public void Dispose()
    {
        if (_ownsBedrockClientFactory && _bedrockClientFactory is IDisposable disposable) disposable.Dispose();

        if (_ownsHttpClient) _httpClient.Dispose();
    }

    /// <inheritdoc/>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // The gauge scope spans the entire request - routing, upstream call, and response streaming -
        // because background work pausing on "in flight" (see InFlightRequestGauge) must stay paused
        // for exactly as long as a client could feel the contention.
        using var _ = _inFlightGauge?.Track();
        await InvokeCoreAsync(context: context, next: next);
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
        _logger.LogInformation(message: "Proxy middleware caught request to {Path}",
            LogRedaction.Sanitize(context.Request.Path.ToString()));

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

        var resolution =
            await _interceptor.ResolveModelRouteAsync(context: context, cancellationToken: context.RequestAborted);

        if (!resolution.IsSuccess)
        {
            await WriteModelNotFoundResponseAsync(context: context, errorMessage: resolution.ErrorMessage!);
            return;
        }

        // docs/adr/0004-.../0005-...: an explicit selection whose target or provider is already
        // circuit-open never reaches the candidate loop at all - RequestInterceptor deliberately left it
        // unsubstituted (so candidates[0] still reports the client's real choice for telemetry), and the
        // truthful message it already resolved is written directly here instead of attempting - or
        // silently substituting away from - a target everyone already knows is untrustworthy.
        if (resolution.ExplicitCircuitTripBlockMessage is { } blockedMessage)
        {
            await WriteCircuitTripBlockedResponseAsync(context: context, message: blockedMessage);
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
                message: "All candidate providers for model {Model} are over their monthly budget; rejecting with 402.",
                LogRedaction.Sanitize(requestedModelName));
            await WriteBudgetExhaustedResponseAsync(context: context, requestedModel: requestedModelName);
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
                if (gate.Predicate(arg1: this, arg2: route, arg3: circuitTarget))
                {
                    gate.LogBlocked(arg1: this, arg2: route);
                    candidateGateBlocked = true;
                    break;
                }

            if (candidateGateBlocked) continue;

            var rewrittenBody = candidates[i].RewrittenBody;
            var isFallback = i > 0;
            var hasNextCandidate = i + 1 < candidates.Count;

            // Every skip/bypass check above has passed, so this candidate is the one about to be attempted -
            // logged at Debug (not Information, unlike the skip/bypass messages above) since it fires on
            // every single attempt, including the common case of a primary succeeding on the first try.
            _logger.LogDebug(
                message:
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
                                      candidates.Skip(i + 1).Any(c => !string.Equals(a: c.Route.Provider,
                                          b: route.Provider, comparisonType: StringComparison.OrdinalIgnoreCase));

            // A provider whose native API shape differs from OpenAI's has an IPayloadTranslator registered
            // (Gemini, Anthropic, and the Bedrock providers today); every other provider has none and keeps
            // the byte-for-byte pass-through path below unchanged.
            _translators.TryGetValue(key: route.Provider, value: out var translator);

            // A provider with no registered translator can still need its *response* normalized, when the
            // model it serves expresses tool calls as text rather than as an OpenAI tool_calls delta
            // (docs/router/tool-call-normalization.md). Unlike every translator above this is selected per
            // (provider, model) and per request rather than by provider key, because a model's tool-call
            // syntax comes from its chat template: one local server serves both a model that needs
            // rewriting and one that must never be scanned. Returning null - the common case - keeps the
            // byte-for-byte pass-through path below exactly as it was.
            translator ??= _toolCallNormalizerFactory.TryCreate(
                route: route,
                requestCarriesTools: candidates[i].CarriesTools,
                requestCarriesToolHistory: candidates[i].CarriesToolHistory,
                requestCarriesResponseFormat: candidates[i].CarriesResponseFormat);

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
                if (await _bedrockInvocationHandler.InvokeAsync(context: context, route: route,
                        translator: bedrockTranslator, rewrittenBody: rewrittenBody,
                        requestedModelName: requestedModelName, isFallback: isFallback,
                        hasNextCandidate: hasNextCandidate, nextProviderDiffers: nextProviderDiffers,
                        taskEmbedding: resolution.TaskEmbedding, routerTokens: resolution.RouterTokens,
                        resolutionReason: resolution.SubstitutionReason, isExploratory: resolution.IsExploratory,
                        propensity: resolution.Propensity, classification: resolution.Classification,
                        taskText: resolution.TaskText, dimBestModel: resolution.DimBestModel)) return;

                continue;
            }

            // When a translator exists, it - not the request path - decides the upstream URL (Gemini
            // encodes the model id + streaming choice in the path) and rewrites the body. Anthropic is
            // dual-mode: the same provider key also carries real Claude Code traffic that is already
            // Anthropic-native, so ShouldTranslate can veto translation for this specific request (by path)
            // even though a translator is registered for the provider.
            if (translator is not null && !translator.ShouldTranslate(context.Request)) translator = null;

            // Target URL, translated body, and the forwarded header set are all UpstreamRequestBuilder's.
            // A fresh message per candidate is mandatory, not incidental: an HttpRequestMessage cannot be
            // sent twice, so the failover path below must rebuild rather than retry this instance.
            var requestMessage = UpstreamRequestBuilder.Build(context: context, route: route, translator: translator,
                rewrittenBody: rewrittenBody);

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage responseMessage;
            try
            {
                responseMessage = await _httpClient.SendAsync(request: requestMessage,
                    completionOption: HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken: context.RequestAborted);
            }
            catch (Exception ex) when (IsTransportOutage(ex: ex, requestAborted: context.RequestAborted))
            {
                // Connection refused/reset, DNS failure, or the HttpClient timeout (not a client abort):
                // the upstream is effectively down. Fail over to the next backup if one remains; otherwise
                // this is the end of the cascade and the client sees a 502.
                _circuitBreaker.RecordFailure(circuitTarget);
                requestMessage.Dispose();
                if (hasNextCandidate)
                {
                    _logger.LogWarning(exception: ex,
                        message:
                        "Upstream provider {Provider} unreachable for model {Model}; failing over to the next backup.",
                        LogRedaction.Sanitize(route.Provider), LogRedaction.Sanitize(route.ModelName));
                    continue;
                }

                _logger.LogWarning(exception: ex,
                    message: "Upstream provider {Provider} unreachable for model {Model}; no backup remains.",
                    LogRedaction.Sanitize(route.Provider), LogRedaction.Sanitize(route.ModelName));
                if (!context.Response.HasStarted)
                    // Deliberately a generic message, not ex.Message: transport-exception text can carry
                    // internal hostnames, DNS/socket details, or configured base URLs. The full exception
                    // is already logged above for operators; the client only needs "upstream unavailable."
                    await WriteUpstreamErrorResponseAsync(context: context,
                        errorMessage: "The upstream provider is unavailable.");

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
            _ = _rateLimitCapture.CaptureAsync(providerKey: route.Provider, headers: responseMessage.Headers,
                cancellationToken: CancellationToken.None);

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
            var shouldPreReadErrorBody = translator?.HandlesEmbeddedErrorAt(statusCode)
                                         ?? statusCode is StatusCodes.Status400BadRequest or 429;

            if (shouldPreReadErrorBody)
            {
                preReadErrorBody = await responseMessage.Content.ReadAsByteArrayAsync(context.RequestAborted);
                if (translator is not null &&
                    translator.TryExtractEmbeddedError(body: preReadErrorBody, error: out var embedded))
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
                statusCode: statusCode,
                preReadErrorBody: preReadErrorBody,
                embeddedErrorMessage: embeddedErrorMessage,
                isProviderAuthFailure: isProviderAuthFailure,
                nextProviderDiffers: nextProviderDiffers,
                isExplicitPrimary: !isFallback && resolution.SubstitutionReason == RoutingSubstitutionReason.None);

            ApplyHealthSignal(verdict: verdict, route: route, circuitTarget: circuitTarget, statusCode: statusCode);

            var shouldRetryThisCandidate = verdict.ShouldRetry;

            if (hasNextCandidate && shouldRetryThisCandidate)
            {
                _logger.LogWarning(
                    message:
                    "Upstream provider {Provider} returned {Status} for model {Model}; failing over to the next backup.",
                    LogRedaction.Sanitize(route.Provider), statusCode, LogRedaction.Sanitize(route.ModelName));
                responseMessage.Dispose();
                requestMessage.Dispose();
                continue;
            }

            // This hop is the one that answers the client: a success, a non-retriable status, or the last
            // candidate. Commit its response and stop.
            using (responseMessage)
            using (requestMessage)
            {
                var written = await _upstreamResponseWriter.WriteAsync(
                    context: context,
                    responseMessage: responseMessage,
                    translator: translator,
                    routingHeaders: new RoutingResponseHeaders(
                        RequestedModel: requestedModelName,
                        RoutedModel: route.ModelName,
                        SubstitutionReason: RequestTelemetryPublisher.ResolveSubstitutionReason(isFallback: isFallback,
                            resolutionReason: resolution.SubstitutionReason).ToString()),
                    preReadErrorBody: preReadErrorBody,
                    embeddedErrorMessage: embeddedErrorMessage,
                    statusCode: statusCode);

                if (!written.Committed)
                    // The writer already sent an error envelope in place of a forward that never happened,
                    // so there is nothing to publish telemetry about.
                    return;

                var capturedResponseBytes = written.CapturedResponseBytes;
                var nativeResponseBytes = written.NativeResponseBytes;
                var tailScanner = written.TailScanner;
                var isStreaming = written.IsStreaming;

                var totalDurationMs = stopwatch.ElapsedMilliseconds;

                _logger.LogDebug(
                    message: "[INTERCEPTOR] Intercepted agent response message: {ResponseBody}",
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
                    await _requestTelemetryPublisher.PublishAsync(context: context, route: route,
                        requestedModelName: requestedModelName, isFallback: isFallback,
                        telemetryShapeProvider: telemetryShapeProvider, rewrittenRequestBody: rewrittenBody,
                        capturedResponseBytes: capturedResponseBytes, nativeResponseBytes: nativeResponseBytes,
                        isStreaming: isStreaming, latencyToHeadersMs: latencyToHeadersMs,
                        totalDurationMs: totalDurationMs, statusCode: statusCode,
                        cancellationToken: context.RequestAborted, upstreamHeaders: responseMessage.Headers,
                        tailScanner: tailScanner, taskEmbedding: resolution.TaskEmbedding,
                        routerTokens: resolution.RouterTokens, resolutionReason: resolution.SubstitutionReason,
                        isExploratory: resolution.IsExploratory, propensity: resolution.Propensity,
                        classification: resolution.Classification, taskText: resolution.TaskText,
                        dimBestModel: resolution.DimBestModel);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(exception: ex,
                        message: "Failed to publish routing telemetry; the forwarded response was unaffected.");
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
                    message:
                    "All candidate providers for model {Model} became over budget mid-request; rejecting with 402.",
                    LogRedaction.Sanitize(requestedModelName));
                await WriteBudgetExhaustedResponseAsync(context: context, requestedModel: requestedModelName);
            }
            else if (candidates.All(c =>
                         !_interceptor.IsProviderEnabled(c.Route.Provider) ||
                         !_interceptor.IsModelEnabled(c.Route.ModelName)))
            {
                _logger.LogWarning(
                    message:
                    "All candidate routes for model {Model} are stopped (provider stopped, model stopped, or not currently reported by its provider's endpoint); rejecting with 503.",
                    LogRedaction.Sanitize(requestedModelName));
                await WriteUpstreamErrorResponseAsync(
                    context: context,
                    errorMessage: "All configured routes for this model are currently stopped.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            else
            {
                _logger.LogWarning(
                    message: "All candidate routes for model {Model} are currently circuit-broken; rejecting with 502.",
                    LogRedaction.Sanitize(requestedModelName));
                await WriteUpstreamErrorResponseAsync(context: context,
                    errorMessage: "All configured routes for this model are currently unavailable.");
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
    private static bool IsTransportOutage(Exception ex, CancellationToken requestAborted)
    {
        return ex switch
        {
            HttpRequestException => true,
            OperationCanceledException => !requestAborted.IsCancellationRequested,
            _ => false
        };
    }

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
    internal static bool IsStreamAbort(Exception ex)
    {
        return ex is OperationCanceledException or IOException or SocketException;
    }

    /// <summary>
    /// Applies an <see cref="UpstreamFailureClassifier"/> verdict: records the circuit-breaker signal, logs
    /// the operator-actionable reason, and updates the Providers tab's live-traffic state. The effectful
    /// half of what used to be one ~130-line if/else chain inside <see cref="InvokeCoreAsync"/> - the
    /// decisions themselves are in <see cref="UpstreamFailureClassifier.Classify"/>, which is pure and
    /// separately tested.
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
                LogProviderWideOutage(cause: verdict.ProviderWideCause, route: route, statusCode: statusCode);
                _circuitBreaker.RecordProviderFailure(route.Provider);

                if (verdict.ProviderWideCause is ProviderWideOutageCause.OutOfCredits)
                    _interactionStatusStore?.RecordLiveTrafficFailure(
                        providerKey: route.Provider,
                        kind: ProviderInteractionKind.OutOfCredits,
                        message: verdict.OutOfCreditsMessage);

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
                    _interactionStatusStore?.RecordLiveTrafficSuccess(providerKey: route.Provider,
                        operation: "Live traffic");

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
                    message:
                    "Upstream provider {Provider} returned 401 Unauthorized for model {Model}; treating as a provider-wide outage (likely an invalid or expired credential) and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.EmbeddedCredentialError:
                _logger.LogError(
                    message:
                    "Upstream provider {Provider} returned an embedded credential error for model {Model} on a non-401 status (Gemini, for example, reports an invalid API key as a 400); treating as a provider-wide outage and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.Forbidden:
                _logger.LogError(
                    message:
                    "Upstream provider {Provider} returned 403 Forbidden for model {Model}; treating as a provider-wide outage (likely a permission or API-key-scope problem) and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.MethodNotAllowed:
                // Seen in production as an Alibaba Cloud WAF block page (an HTML "access blocked" response,
                // not a real API error) served instead of reaching the model API at all - a gateway-level
                // rejection of this request, not evidence about the request's actual validity.
                _logger.LogError(
                    message:
                    "Upstream provider {Provider} returned 405 Method Not Allowed for model {Model}; treating as a provider-wide gateway/WAF block and bypassing every model on this provider until it recovers.",
                    provider,
                    model);
                break;

            case ProviderWideOutageCause.OutOfCredits:
                _logger.LogError(
                    message:
                    "Upstream provider {Provider} is out of credits for model {Model} (status {Status}); treating as a provider-wide outage and bypassing every model on this provider until it recovers.",
                    provider,
                    model,
                    statusCode);
                break;
        }
    }

    /// <summary>
    /// Gate (1) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: <see langword="true"/> when
    /// <paramref name="route"/>'s provider is over its monthly budget.
    /// </summary>
    private bool IsBudgetGateBlocked(ResolvedModelRoute route)
    {
        return _budgetStore is not null && _budgetStore.IsBreached(route.Provider);
    }

    /// <summary>
    /// Gate (2) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: <see langword="true"/> when
    /// <paramref name="route"/>'s provider is stopped (Governance &gt; Providers).
    /// </summary>
    private bool IsProviderDisabledGateBlocked(ResolvedModelRoute route)
    {
        return !_interceptor.IsProviderEnabled(route.Provider);
    }

    /// <summary>
    /// Gate (3) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: <see langword="true"/> when
    /// <paramref name="route"/>'s model is stopped or no longer reported by its provider's endpoint.
    /// </summary>
    private bool IsModelDisabledGateBlocked(ResolvedModelRoute route)
    {
        return !_interceptor.IsModelEnabled(route.ModelName);
    }

    /// <summary>
    /// Gate (4) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: the read-only circuit
    /// pre-check that MUST run before gates (5)/(6) - see the ordering note above the loop in
    /// <see cref="InvokeCoreAsync"/> for why.
    /// </summary>
    private bool IsCircuitOpenPreCheckGateBlocked(ResolvedModelRoute route, CircuitBreakerTargetKey circuitTarget)
    {
        return _circuitBreaker.IsOpen(circuitTarget) || _circuitBreaker.IsProviderOpen(route.Provider);
    }

    /// <summary>
    /// Gate (5) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: the target-level circuit breaker,
    /// which may claim a half-open probe slot - see gate (4)'s remarks.
    /// </summary>
    private bool IsCircuitBypassGateBlocked(CircuitBreakerTargetKey circuitTarget)
    {
        return _circuitBreaker.ShouldBypass(circuitTarget);
    }

    /// <summary>
    /// Gate (6) of <see cref="InvokeCoreAsync"/>'s candidate pre-flight sequence: the provider-wide circuit breaker,
    /// which may claim a half-open probe slot - see gate (4)'s remarks.
    /// </summary>
    private bool IsCircuitBypassProviderGateBlocked(ResolvedModelRoute route)
    {
        return _circuitBreaker.ShouldBypassProvider(route.Provider);
    }

    /// <summary>
    /// Writes a client-facing error envelope for a failed upstream call (a Bedrock SDK failure, or an exhausted
    /// transport-outage cascade), matching <see cref="WriteModelNotFoundResponseAsync"/>'s shape. Defaults to 502 (the request
    /// was valid; the upstream call failed) rather than 400 (a malformed/unknown-model client request);
    /// <paramref name="statusCode"/> lets a caller override this - e.g. 401 for a Bedrock credential-resolution failure
    /// treated like the HTTP path's 401 handling. Callers pass a client-safe <paramref name="errorMessage"/> - never a raw
    /// transport-exception message, which can leak infrastructure detail.
    /// </summary>
    internal static async Task WriteUpstreamErrorResponseAsync(HttpContext context, string errorMessage,
        int statusCode = StatusCodes.Status502BadGateway)
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

        await context.Response.WriteAsync(text: JsonSerializer.Serialize(payload),
            cancellationToken: context.RequestAborted);
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
    /// Builds the set of hop-by-hop header names to strip: the fixed RFC 7230 set, plus any additional header
    /// names nominated by a <c>Connection</c> header value (e.g. <c>Connection: Foo</c> makes <c>Foo</c> hop-by-hop).
    /// </summary>
    internal static HashSet<string> GetHopByHopHeaderNames(IEnumerable<string>? connectionHeaderValues)
    {
        var names = new HashSet<string>(collection: HopByHopHeaders, comparer: StringComparer.OrdinalIgnoreCase);

        if (connectionHeaderValues is null) return names;

        foreach (var value in connectionHeaderValues)
            foreach (var token in value.Split(',',
                         options: StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                names.Add(token);

        return names;
    }

    /// <summary>
    /// Writes an OpenAI-shaped <c>{"error": {...}}</c> envelope: the shared shape every client-facing error
    /// response in this class uses, differing only by status code, <paramref name="type"/>, message, and an
    /// optional <paramref name="param"/> (omitted from the JSON entirely when <see langword="null"/>, via
    /// <see cref="ErrorDetail.Param"/>'s <c>WhenWritingNull</c> condition). <paramref name="statusCode"/> is
    /// echoed back as the envelope's string <c>code</c> field, matching every existing call site.
    /// </summary>
    private static async Task WriteErrorResponseAsync(HttpContext context, int statusCode, string type, string message,
        string? param = null)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = new ErrorEnvelope(new ErrorDetail(Message: message, Type: type, Param: param,
            Code: statusCode.ToString(CultureInfo.InvariantCulture)));

        await context.Response.WriteAsync(text: JsonSerializer.Serialize(payload),
            cancellationToken: context.RequestAborted);
    }

    /// <summary>Writes a 400 response in an OpenAI-shaped error envelope for a request whose model could not be resolved.</summary>
    private static Task WriteModelNotFoundResponseAsync(HttpContext context, string errorMessage)
    {
        return WriteErrorResponseAsync(context: context, statusCode: StatusCodes.Status400BadRequest,
            type: "invalid_request_error", message: errorMessage, param: "model");
    }

    /// <summary>
    /// Writes a 503 response in an OpenAI-shaped error envelope when the GUI system tray's kill switch
    /// (<see cref="Router.IRoutingGate"/>) has routing disabled. Matches
    /// <see cref="WriteModelNotFoundResponseAsync"/>'s envelope shape but as 503 (Service Unavailable): the
    /// request itself may be perfectly valid, it was refused solely because an operator paused routing.
    /// </summary>
    private static Task WriteRoutingDisabledResponseAsync(HttpContext context)
    {
        return WriteErrorResponseAsync(context: context, statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "routing_disabled", message: "Routing is currently disabled.");
    }

    /// <summary>
    /// Writes a client-facing 402 error envelope when every candidate provider for a request is over its
    /// monthly budget. Matches <see cref="WriteModelNotFoundResponseAsync"/>'s OpenAI-shaped envelope but as
    /// a 402 (Payment Required): the request was valid and the model known - it was refused because the
    /// operator's spend cap is exhausted, which is a distinct, retriable-next-month condition, not a
    /// malformed request or an upstream outage.
    /// </summary>
    private static Task WriteBudgetExhaustedResponseAsync(HttpContext context, string requestedModel)
    {
        return WriteErrorResponseAsync(
            context: context,
            statusCode: StatusCodes.Status402PaymentRequired,
            type: "budget_exhausted",
            message: $"model '{requestedModel}' and all its fallbacks are over their configured monthly budget.",
            param: "model");
    }

    /// <summary>
    /// Writes a client-facing 503 error envelope for an explicit selection whose target or provider is
    /// already circuit-open from an earlier request (docs/adr/0004-surface-out-of-credits-provider-
    /// failures-on-the-providers-tab.md, docs/adr/0005-protect-explicit-provider-selections-from-silent-
    /// substitution-on-any-circuit-trip.md). Unlike <see cref="WriteBudgetExhaustedResponseAsync"/>'s 402
    /// (a hard operator-configured cap) this is 503 (Service Unavailable): the client's own selection was
    /// valid, the router simply already knows this specific target or provider isn't answering right now
    /// and never made a network call to find out again.
    /// </summary>
    private static Task WriteCircuitTripBlockedResponseAsync(HttpContext context, string message)
    {
        return WriteErrorResponseAsync(context: context, statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "invalid_request_error", message: message);
    }

    /// <summary>The top-level <c>{"error": {...}}</c> envelope <see cref="WriteErrorResponseAsync"/> writes.</summary>
    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] ErrorDetail Error);

    /// <summary>The body of an OpenAI-shaped error envelope written by <see cref="WriteErrorResponseAsync"/>.</summary>
    private sealed record ErrorDetail(
        [property: JsonPropertyName("message")]
        string Message,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("param")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Param,
        [property: JsonPropertyName("code")] string Code);
}