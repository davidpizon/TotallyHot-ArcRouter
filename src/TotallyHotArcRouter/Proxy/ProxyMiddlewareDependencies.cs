using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Quality.Ingress;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// The optional collaborators <see cref="ProxyMiddleware"/> can be given, carried as one named object so
/// its constructor does not take two dozen positional nullable arguments a caller can silently transpose.
/// </summary>
/// <remarks>
/// Deliberately flat, mirroring <see cref="Management.ManagementFacadeDependencies"/> rather than
/// <see cref="ProxyServerDependencies"/>'s per-feature groups: every member here is checked independently
/// at its own use site, and none are all-or-nothing pairs the way <see cref="ProxyServerDependencies"/>'s
/// admin-surface groups are.
/// </remarks>
public sealed record ProxyMiddlewareDependencies
{
    /// <summary>Optional session-id resolver; defaults to <see cref="SessionIdResolver"/>.</summary>
    public ISessionIdResolver? SessionIdResolver { get; init; }

    /// <summary>
    /// Optional continuity matcher, used when <see cref="SessionIdResolver"/> finds nothing; defaults to a fresh
    /// <see cref="MessageHistoryContinuityMatcher"/> private to the instance.
    /// </summary>
    public IConversationContinuityMatcher? ContinuityMatcher { get; init; }

    /// <summary>Optional turn tracker; defaults to a fresh <see cref="ConversationTurnTracker"/> private to the instance.</summary>
    public IConversationTurnTracker? TurnTracker { get; init; }

    /// <summary>Optional usage extractor; defaults to <see cref="UsageExtractor"/>.</summary>
    public IUsageExtractor? UsageExtractor { get; init; }

    /// <summary>Optional response-text extractor; defaults to <see cref="ResponseTextExtractor"/>.</summary>
    public IResponseTextExtractor? ResponseTextExtractor { get; init; }

    /// <summary>
    /// Optional telemetry publisher; defaults to a fresh <see cref="TelemetryPublisher"/> backed by a private,
    /// unshared <see cref="TelemetryBroadcaster"/> (a safe no-op, since nothing is ever registered to receive from it).
    /// </summary>
    public ITelemetryPublisher? TelemetryPublisher { get; init; }

    /// <summary>
    /// Optional quality-verifier ingress façade; when supplied, completed responses are enqueued for off-path
    /// grading. Best-effort and non-blocking; defaults to <see langword="null"/> (disabled).
    /// </summary>
    public IQualityIngress? QualityIngress { get; init; }

    /// <summary>
    /// Optional running-spend tracker; defaults to <see cref="NullSpendTracker"/> (a safe no-op) so existing
    /// callers/tests that don't need it are unaffected.
    /// </summary>
    public ISpendTracker? SpendTracker { get; init; }

    /// <summary>
    /// Optional catalog price lookup (docs/router/model-price-catalog.md); when supplied, a paid route's per-request
    /// cost is estimated from the auto-refreshed price catalog. Defaults to <see langword="null"/> (disabled), leaving
    /// paid-model cost unknown as before.
    /// </summary>
    public IModelPriceLookup? PriceLookup { get; init; }

    /// <summary>
    /// Optional per-provider payload translators (docs/router/unified-api-translation.md), keyed by
    /// provider name. A provider present here has its request/response/stream translated to and from
    /// OpenAI's shape (Gemini and Bedrock providers always; Anthropic when
    /// <see cref="IPayloadTranslator.ShouldTranslate"/> allows it for the request); a provider absent
    /// here, or whose translator vetoes this request, is forwarded byte-for-byte, exactly as before.
    /// Defaults to an empty map (all providers pass through unchanged).
    /// </summary>
    public IReadOnlyDictionary<string, IPayloadTranslator>? Translators { get; init; }

    /// <summary>
    /// Optional factory for the Amazon Bedrock Runtime SDK client used by any translator implementing
    /// <see cref="Bedrock.IBedrockPayloadTranslator"/>. In the real app this is always supplied via DI (a
    /// shared singleton the container owns and disposes); when omitted (direct construction outside DI,
    /// e.g. a caller that never touches the Bedrock path), <see cref="ProxyMiddleware"/> builds and owns
    /// its own <see cref="Bedrock.BedrockRuntimeClientFactory"/> and disposes it in
    /// <see cref="ProxyMiddleware.Dispose"/>. Overridable for tests, which substitute a fake
    /// <c>IAmazonBedrockRuntime</c> so no live AWS call is made.
    /// </summary>
    public IBedrockRuntimeClientFactory? BedrockClientFactory { get; init; }

    /// <summary>
    /// Optional per-provider monthly budget store (Governance &gt; Providers). When supplied, a provider whose cap is
    /// exhausted is skipped for the request, an all-breached request is rejected with 402, and each served request's usage is
    /// recorded against the serving provider. Defaults to <see langword="null"/> (no budgets enforced or recorded), so
    /// existing callers/tests are unaffected.
    /// </summary>
    public IBudgetEnforcer? BudgetStore { get; init; }

    /// <summary>
    /// Optional per-upstream-target circuit breaker (<c>docs/router/agent-resilience-strategies.md</c>). Must be the
    /// <em>same</em> instance given to the <see cref="RequestInterceptor"/> this middleware wraps (see
    /// <c>ServiceCollectionExtensions</c>'s DI wiring) - this class is what records the successes/failures
    /// <see cref="RequestInterceptor"/> reads back when ranking candidates. Defaults to a fresh, always-CLOSED instance when
    /// omitted, which is behaviorally inert (existing callers/tests unaffected) but decoupled from any interceptor-side
    /// instance, so circuit state recorded here would never be seen there.
    /// </summary>
    public ICircuitBreaker? CircuitBreaker { get; init; }

    /// <summary>
    /// Optional per-request tool-call normalization (<c>docs/router/tool-call-normalization.md</c> Phase
    /// 4), consulted for any candidate with no other translator registered: it decides from the
    /// (provider, model) capability row and whether the request carried <c>tools</c> whether the response
    /// needs a dialect scan at all, and rewrites a dialect-framed tool call into a real <c>tool_calls</c>
    /// shape. Replaces the provider-wide echo guard of <c>unified-api-translation.md</c> §4.5. In the real
    /// app this is always supplied via DI (a shared singleton reading the capability store); when omitted
    /// (direct construction outside DI), <see cref="ProxyMiddleware"/> builds a store-less one - so a
    /// tools-carrying request is still normalized with the union of dialects, but nothing is classified or
    /// persisted, mirroring <see cref="CircuitBreaker"/>'s "behaviorally inert when defaulted" pattern.
    /// </summary>
    public ToolCallNormalizerFactory? ToolCallNormalizerFactory { get; init; }

    /// <summary>
    /// Optional capture for upstream <c>anthropic-ratelimit-*</c> response headers (
    /// <c>docs/router/anthropic-reported-usage-plan.md</c> §5), invoked as soon as each attempt's response headers arrive.
    /// Defaults to a no-op, so existing callers/tests are unaffected.
    /// </summary>
    public IRateLimitHeaderCapture? RateLimitCapture { get; init; }

    /// <summary>
    /// Optional durable usage ledger (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2), recorded to
    /// immediately after <see cref="BudgetStore"/> on the request path. When <see langword="null"/> (e.g. tests constructing
    /// this type directly), no ledger row is written - the rest of telemetry is unaffected.
    /// </summary>
    public IUsageLedger? UsageLedger { get; init; }

    /// <summary>
    /// Optional bridge (docs/router/live-feedback-learning-plan.md Phase 2c) between
    /// <see cref="RequestInterceptor"/>'s Phase 2b embedding computation and the request's later-arriving
    /// verifier score, which is only correlated by the id computed at session/turn resolution - the
    /// earliest point that id is actually known, since <see cref="RequestInterceptor.ResolveModelRouteAsync"/>
    /// runs before it. Defaults to <see langword="null"/> (no entries recorded), so existing
    /// callers/tests are unaffected.
    /// </summary>
    public PendingTaskEmbeddingCache? PendingTaskEmbeddingCache { get; init; }

    /// <summary>
    /// Supplies <see cref="Models.RoutingOptions.SelfHostedRouterPricePerMillionTokens"/>, the rate the
    /// router's own token consumption is charged at when published on
    /// <see cref="Telemetry.RoutingTelemetryEvent.RouterCostUsd"/>. When <see langword="null"/> (direct
    /// construction outside DI) the compiled-in default applies, so the figure is still real rather than
    /// suppressed - unlike the optional collaborators above, there is no "unavailable" state for a static
    /// amortization rate.
    /// </summary>
    public IOptions<RoutingOptions>? RoutingOptions { get; init; }

    /// <summary>
    /// Optional bridge (docs/router/self-organizing-classification-plan.md Phase T1c) between this
    /// request's estimated cost and its later-arriving verifier score - mirrors
    /// <see cref="PendingTaskEmbeddingCache"/>'s role exactly, for a different value. Defaults to
    /// <see langword="null"/> (no entries recorded), so existing callers/tests are unaffected.
    /// </summary>
    public PendingRequestCostCache? PendingRequestCostCache { get; init; }

    /// <summary>
    /// Optional bridge (docs/router/self-organizing-classification-plan.md Phase T1c) between this
    /// request's exploration provenance (is-exploratory/propensity, resolved earlier by
    /// <see cref="RequestInterceptor"/>) and its later-arriving verifier score. Defaults to
    /// <see langword="null"/> (no entries recorded), so existing callers/tests are unaffected.
    /// </summary>
    public PendingRequestProvenanceCache? PendingRequestProvenanceCache { get; init; }

    /// <summary>
    /// Optional bridge (docs/router/geval-shadow-scoring-plan.md §Raw-text preservation) between this
    /// request's already-extracted response text and the shadow judge's later-arriving background job -
    /// mirrors <see cref="PendingTaskEmbeddingCache"/>'s role exactly, for a different value. Populated at
    /// the same point <see cref="ResponseTextExtractor"/>'s result is already in hand, so this adds
    /// retention, not parsing. Defaults to <see langword="null"/> (no entries recorded), so existing
    /// callers/tests are unaffected.
    /// </summary>
    public PendingResponseTextCache? PendingResponseTextCache { get; init; }

    /// <summary>
    /// Optional bridge between this request's newest user message and the judge's later-arriving background
    /// job, mirroring <see cref="PendingResponseTextCache"/>'s role exactly for the prompt half of the pair
    /// instead of the response half. Populated at the same point the response text is, so the judge can
    /// grade the response against the requirement it was written for. Defaults to <see langword="null"/> (no
    /// entries recorded), so existing callers/tests are unaffected.
    /// </summary>
    public PendingPromptCache? PendingPromptCache { get; init; }

    /// <summary>
    /// Optional opt-in transcript store (docs/router/self-organizing-classification-plan.md Phase
    /// T1a/T1b). When supplied and transcript capture is enabled, one row is inserted per served request
    /// with the prompt/response text, classification, cost, and provenance already in scope at this
    /// point; the score is backfilled later by <see cref="Transcripts.TranscriptScoreObserver"/>. Defaults
    /// to <see langword="null"/> (no rows written), matching the feature's opt-in-and-off-by-default posture.
    /// </summary>
    public ITranscriptStore? TranscriptStore { get; init; }

    /// <summary>
    /// Optional in-flight request gauge (docs/router/routing-roi-regret-plan.md). When supplied, every
    /// request is counted for the full duration of <see cref="ProxyMiddleware.InvokeAsync"/> so background
    /// analysis work (the taxonomy-comparison drain) can hard-pause while traffic is being served.
    /// Defaults to <see langword="null"/> (no tracking), so existing callers/tests are unaffected.
    /// </summary>
    public InFlightRequestGauge? InFlightGauge { get; init; }

    /// <summary>
    /// Optional live routing-options monitor (docs/router/self-organizing-classification-plan.md Phase
    /// T6), consulted alongside <see cref="Transcripts.TranscriptOptions.Enabled"/> at the
    /// transcript-insert site so a <see cref="Models.RoutingOptions.EnableAdaptiveRouting"/> toggle stops
    /// (or resumes) new transcript writes without a restart. Defaults to <see langword="null"/>, which is
    /// treated as adaptive routing being disabled - matching
    /// <see cref="Models.RoutingOptions.EnableAdaptiveRouting"/>'s own off-by-default coded value.
    /// </summary>
    public IOptionsMonitor<RoutingOptions>? RoutingOptionsMonitor { get; init; }

    /// <summary>
    /// Optional live shadow-judge options monitor, consulted at the response-text retention site so raw
    /// response text is held for judging only while <see cref="Judge.JudgeOptions.Enabled"/> is actually
    /// on. Read live rather than captured because that flag is operator-toggleable at runtime, and this is
    /// the gate that decides whether raw text is retained at all - it must go off the moment the operator
    /// says so, not at the next restart. Defaults to <see langword="null"/>, treated as the judge being
    /// disabled, matching <see cref="Judge.JudgeOptions.Enabled"/>'s own off-by-default coded value.
    /// </summary>
    public IOptionsMonitor<JudgeOptions>? JudgeOptionsMonitor { get; init; }

    /// <summary>
    /// Optional runtime kill switch, toggled from the GUI system tray via
    /// <see cref="Router.RoutingGateAdminGrpcService"/>. When <see cref="Router.IRoutingGate.IsEnabled"/>
    /// is <see langword="false"/>, every LLM-forwarding request is rejected with 503 before routing is
    /// attempted; <see langword="null"/> (the default) means routing is always accepted, matching the
    /// enabled-by-default coded value <see cref="Router.RoutingGateStore"/> itself falls back to.
    /// </summary>
    public IRoutingGate? RoutingGate { get; init; }

    /// <summary>
    /// Optional source of each model's detected tool-call dialect, used only to describe models on
    /// <c>POST /api/show</c>. <see langword="null"/> (the default) is behaviorally inert: every model
    /// reads as unclassified, which
    /// <see cref="Translation.ToolCalling.OllamaModelCapabilities.ForDialect"/> already treats as
    /// tool-capable, so the declared capabilities are unchanged.
    /// </summary>
    public IToolCallCapabilityStore? CapabilityStore { get; init; }

    /// <summary>
    /// Optional source of each model's probed context window, used only to populate
    /// <c>POST /api/show</c>'s <c>model_info</c>. <see langword="null"/> (the default) is behaviorally
    /// inert: <c>model_info</c> is omitted entirely, exactly as it was before this was wired up.
    /// </summary>
    public IModelContextWindowStore? ContextWindowStore { get; init; }

    /// <summary>
    /// Optional per-provider admin-action/live-traffic status store
    /// (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md,
    /// docs/adr/0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-
    /// trip.md). On a classified out-of-credits response, this records the LiveTraffic-track failure (and
    /// success on every subsequent 2xx) that the Providers tab and <see cref="RequestInterceptor"/>'s
    /// explicit-selection protection both read from. Must be the <em>same</em> instance given to
    /// <see cref="RequestInterceptor"/> and <c>ManagementFacade</c> (see <c>ServiceCollectionExtensions</c>'s
    /// DI wiring) - the same sharing requirement <see cref="CircuitBreaker"/> already has. Defaults to
    /// <see langword="null"/>, which is behaviorally inert (no LiveTraffic state is ever recorded).
    /// </summary>
    public IProviderInteractionStatusStore? InteractionStatusStore { get; init; }
}