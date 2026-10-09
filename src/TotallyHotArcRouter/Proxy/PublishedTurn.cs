using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// What <see cref="RequestTelemetryPublisher.PublishAsync"/> worked out for one served request: the session and
/// turn identity, the usage and cost it extracted, and the text it pulled out of the request and reply. Returned
/// so session capture can snapshot these facts at the moment they were computed, without the publisher taking a
/// dependency on session storage (the plan keeps its constructor from widening).
/// </summary>
/// <param name="SessionId">The client's session id, explicit or synthesized.</param>
/// <param name="TurnNumber">The turn within <paramref name="SessionId"/>.</param>
/// <param name="IsSessionSynthesized">Whether the router made the session id up.</param>
/// <param name="CorrelationId"><c>{SessionId}:{TurnNumber}</c>, shared with the telemetry event.</param>
/// <param name="RequestedModel">The model the client literally asked for.</param>
/// <param name="SubstitutionReason">Why the served model differs from the requested one.</param>
/// <param name="Provider">The provider that served the request.</param>
/// <param name="RoutedModel">The client-facing name of the model that served it.</param>
/// <param name="ProviderModelId">The id the provider knows the model by.</param>
/// <param name="IsFallback">Whether a fallback candidate served the request.</param>
/// <param name="IsExploratory">Whether routing explored rather than exploited.</param>
/// <param name="Propensity">The probability routing assigned to its choice.</param>
/// <param name="Classification">The request's classification labels, when it was classified.</param>
/// <param name="PromptTokens">Input tokens, or <see langword="null"/> when usage could not be read.</param>
/// <param name="CompletionTokens">Output tokens, or <see langword="null"/> when usage could not be read.</param>
/// <param name="CacheCreationTokens">Cache-write tokens, when reported.</param>
/// <param name="CacheReadTokens">Cache-read tokens, when reported.</param>
/// <param name="EstimatedCostUsd">The estimated cost, or <see langword="null"/> when unknown.</param>
/// <param name="CostConfidence">How much to trust <paramref name="EstimatedCostUsd"/>.</param>
/// <param name="StatusCode">The upstream HTTP status.</param>
/// <param name="LatencyToHeadersMs">Milliseconds until the upstream's headers arrived.</param>
/// <param name="TotalDurationMs">Milliseconds until the response was fully relayed.</param>
/// <param name="IsStreaming">Whether the response was a server-sent-event stream.</param>
/// <param name="NewestUserMessage">The newest user message in the request, or <see langword="null"/> when none could be extracted.</param>
/// <param name="ResponseText">The reply text extracted from the captured response, or <see langword="null"/> when none could be.</param>
internal sealed record PublishedTurn(
    string SessionId,
    int TurnNumber,
    bool IsSessionSynthesized,
    string CorrelationId,
    string RequestedModel,
    RoutingSubstitutionReason SubstitutionReason,
    string Provider,
    string RoutedModel,
    string ProviderModelId,
    bool IsFallback,
    bool IsExploratory,
    double Propensity,
    Router.Classification.RequestClassification? Classification,
    int? PromptTokens,
    int? CompletionTokens,
    int? CacheCreationTokens,
    int? CacheReadTokens,
    decimal? EstimatedCostUsd,
    CostConfidence CostConfidence,
    int StatusCode,
    long LatencyToHeadersMs,
    long TotalDurationMs,
    bool IsStreaming,
    string? NewestUserMessage,
    string? ResponseText)
{
    /// <summary>
    /// Gets the failure of the best-effort telemetry side effects (spend, budget, ledger, transcript row, event
    /// publication), or <see langword="null"/> when they all ran. Carried here rather than thrown, so a telemetry
    /// failure never stops session capture from storing a turn that was served and relayed.
    /// </summary>
    public Exception? TelemetryFailure { get; init; }
}
