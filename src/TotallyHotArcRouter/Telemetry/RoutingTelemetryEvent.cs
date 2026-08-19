namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One completed routed request, broadcast to connected dashboards via <see cref="TelemetryBroadcaster"/>.
/// This is the live counterpart to the GUI's mock <c>ConversationTurn</c> shape - see
/// <c>docs/gui/backlog.md</c> for the mapping decisions (e.g. "Agent" is the selected model, since
/// the router has no separate agent concept).
/// </summary>
/// <param name="SessionId">
/// The resolved (or synthesized, when none could be resolved - see <paramref name="IsSessionSynthesized"/>) session id.
/// </param>
/// <param name="TurnNumber">1-based position of this request within its session.</param>
/// <param name="IsSessionSynthesized">
/// <see langword="true"/> when no explicit session id was found in the request (see
/// <see cref="ISessionIdResolver"/>) and <see cref="IConversationContinuityMatcher"/> was used instead
/// - either matching this request to a previously-tracked conversation by its message history (so
/// <paramref name="TurnNumber"/> can still be greater than 1) or, failing that, generating a fresh
/// single-use id.
/// </param>
/// <param name="RequestedModel">
/// The client's literal <c>model</c> string from the request body - captured before any router
/// substitution (auto-select, an unresolved name, a stopped model, an open circuit, or failover). See
/// <paramref name="RoutedModel"/> for what actually served the request, and
/// <c>docs/router/orchestrator-live-path-plan.md</c> §M2.2 for the three-field distinction.
/// </param>
/// <param name="ResolvedModel">The upstream provider's model id the request was actually forwarded as.</param>
/// <param name="Provider">The provider key the request was routed to.</param>
/// <param name="IsFallback">Whether this request was served by fallback routing.</param>
/// <param name="PromptTokens">Extracted prompt/input token count, or <see langword="null"/> if usage couldn't be determined.</param>
/// <param name="CompletionTokens">Extracted completion/output token count, or <see langword="null"/> if usage couldn't be determined.</param>
/// <param name="EstimatedCostUsd">Estimated USD cost - <c>0</c> when the route's provider is free (<see cref="TotallyHot.ArcRouter.Models.ProviderOptions.IsFree"/>), otherwise <see langword="null"/>: no price data source exists until the price catalog lands (see <c>docs/router/model-price-catalog.md</c>). Also <see langword="null"/> when usage couldn't be extracted.</param>
/// <param name="IsStreaming">Whether the response was a streaming (SSE) response.</param>
/// <param name="LatencyToHeadersMs">Milliseconds from sending the upstream request to receiving its response headers.</param>
/// <param name="TotalDurationMs">Milliseconds from sending the upstream request to the response body finishing.</param>
/// <param name="StatusCode">The upstream response's HTTP status code.</param>
/// <param name="TimestampUtc">When the request was routed.</param>
/// <param name="CacheCreationTokens">
/// Input tokens written to a new prompt cache entry (<see cref="UsageInfo.CacheCreationTokens"/>), or
/// <see langword="null"/> if usage couldn't be determined.
/// </param>
/// <param name="CacheReadTokens">
/// Input tokens served from an existing prompt cache entry (<see cref="UsageInfo.CacheReadTokens"/>), or
/// <see langword="null"/> if usage couldn't be determined.
/// </param>
/// <param name="CostConfidence">How <paramref name="EstimatedCostUsd"/> was arrived at; see <see cref="Telemetry.CostConfidence"/> (§5.6).</param>
/// <param name="RequestSummary">
/// The newest user message's text (see <see cref="RequestTextExtractor"/>), truncated to
/// <see cref="TextTruncator.DefaultMaxLength"/> characters, or <see langword="null"/> if there was no
/// user message or its content couldn't be read as text. Deliberately not the whole resent
/// conversation history - see <see cref="RequestTextExtractor"/>'s remarks.
/// </param>
/// <param name="ResponseSummary">
/// The assistant's reply text (see <see cref="IResponseTextExtractor"/>), truncated to
/// <see cref="TextTruncator.DefaultMaxLength"/> characters, or <see langword="null"/> if the provider
/// is unsupported or the response body/stream had no extractable text (e.g. a tool-only response).
/// </param>
/// <param name="CorrelationId">
/// A stable id for this routed request (currently <c>{SessionId}:{TurnNumber}</c>). Links this event to a
/// later off-path sandbox signal (see <c>SandboxSignalEvent</c>), or <see langword="null"/> when not set.
/// </param>
/// <param name="RouterTokens">
/// Tokens the <b>router itself</b> consumed deciding where to send this request - today the embedding
/// model's tokenized sequence length (see <c>Router.Embeddings.EmbeddingResult</c>). <c>0</c> is a real
/// measurement, not a missing one: it means no embedding was computed for this request (no embedding
/// client configured, still warming up, budget exceeded, or no extractable task text), so the router
/// genuinely spent nothing.
/// </param>
/// <param name="RouterCostUsd">
/// USD cost of <paramref name="RouterTokens"/> at
/// <see cref="TotallyHot.ArcRouter.Models.RoutingOptions.SelfHostedRouterPricePerMillionTokens"/>.
/// Recorded separately from <paramref name="EstimatedCostUsd"/> rather than folded into it, because the
/// two answer different questions - what the request cost upstream, versus what routing it cost us - and
/// only their sum is the honest denominator for a savings figure (research-doc §5.1 charges router tokens
/// into <c>TotTok</c>). Never <see langword="null"/>: unlike a provider price, this rate is always known,
/// so zero tokens yield a known zero rather than an unknown.
/// </param>
/// <param name="RoutedModel">
/// The router's client-facing name for the model that actually served the request (post-failover,
/// post-substitution) - <c>ResolvedModelRoute.ModelName</c> of the winning candidate. Equal to
/// <paramref name="RequestedModel"/> when no substitution occurred. See
/// <paramref name="SubstitutionReason"/> for why they differ when they do.
/// </param>
/// <param name="SubstitutionReason">
/// Why <paramref name="RoutedModel"/> differs from <paramref name="RequestedModel"/> -
/// <see cref="RoutingSubstitutionReason.None"/> when it doesn't.
/// </param>
public sealed record RoutingTelemetryEvent(
    string SessionId,
    int TurnNumber,
    bool IsSessionSynthesized,
    string RequestedModel,
    string ResolvedModel,
    string Provider,
    bool IsFallback,
    int? PromptTokens,
    int? CompletionTokens,
    decimal? EstimatedCostUsd,
    bool IsStreaming,
    long LatencyToHeadersMs,
    long TotalDurationMs,
    int StatusCode,
    DateTimeOffset TimestampUtc,
    string RoutedModel,
    int? CacheCreationTokens = null,
    int? CacheReadTokens = null,
    CostConfidence CostConfidence = CostConfidence.Unknown,
    string? RequestSummary = null,
    string? ResponseSummary = null,
    string? CorrelationId = null,
    int RouterTokens = 0,
    decimal RouterCostUsd = 0m,
    RoutingSubstitutionReason SubstitutionReason = RoutingSubstitutionReason.None);

