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
/// <param name="RequestedModel">The client-facing model name from the request body.</param>
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
    string? RequestSummary = null,
    string? ResponseSummary = null,
    string? CorrelationId = null);

