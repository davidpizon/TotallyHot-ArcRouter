namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One request's usage, as recorded into <c>usage_ledger</c> (<see cref="IUsageLedger"/>). Mirrors the
/// fields <see cref="RoutingTelemetryEvent"/> already carries for a routed request, plus the extras the
/// durable store needs: an optional upstream request id for dedup, and a cost-confidence label (a fixed
/// <c>"Unknown"</c> placeholder until Phase 3's <c>CostConfidence</c> enum lands - see
/// <c>docs/router/token-tracking-implementation-plan.md</c> Phase 3).
/// </summary>
/// <param name="SessionId">The resolved (or synthesized) session id - see <see cref="RoutingTelemetryEvent.SessionId"/>.</param>
/// <param name="TurnNumber">1-based position of this request within its session.</param>
/// <param name="Provider">The provider key that actually served the request.</param>
/// <param name="RequestedModel">The client-facing model name from the request body.</param>
/// <param name="ResolvedModel">The upstream provider's model id the request was actually forwarded as.</param>
/// <param name="PromptTokens">Extracted prompt/input token count, or <see langword="null"/> if usage couldn't be determined.</param>
/// <param name="CompletionTokens">Extracted completion/output token count, or <see langword="null"/> if usage couldn't be determined.</param>
/// <param name="CacheCreationTokens">Input tokens written to a new prompt cache entry, or <see langword="null"/> if usage couldn't be determined.</param>
/// <param name="CacheReadTokens">Input tokens served from an existing prompt cache entry, or <see langword="null"/> if usage couldn't be determined.</param>
/// <param name="EstimatedCostUsd">Estimated USD cost, or <see langword="null"/> when unknown - never a silent zero (see <c>ModelPrice.EstimateCost</c>).</param>
/// <param name="CostConfidence">
/// How much to trust <paramref name="EstimatedCostUsd"/>. Always <c>"Unknown"</c> in Phase 2; Phase 3
/// replaces this placeholder with a real <c>CostConfidence</c> value serialized to string.
/// </param>
/// <param name="OccurredAtUtc">When the request completed (matches <see cref="RoutingTelemetryEvent.TimestampUtc"/>).</param>
/// <param name="RequestId">
/// The upstream provider's own request id (from a <c>request-id</c>/<c>x-request-id</c> response header),
/// when one was captured. Preferred over the composite hash as the dedup key basis - see
/// <see cref="IUsageLedger"/>'s remarks.
/// </param>
public sealed record UsageLedgerEntry(
    string SessionId,
    int TurnNumber,
    string Provider,
    string RequestedModel,
    string ResolvedModel,
    int? PromptTokens,
    int? CompletionTokens,
    int? CacheCreationTokens,
    int? CacheReadTokens,
    decimal? EstimatedCostUsd,
    string CostConfidence,
    DateTimeOffset OccurredAtUtc,
    string? RequestId = null);
