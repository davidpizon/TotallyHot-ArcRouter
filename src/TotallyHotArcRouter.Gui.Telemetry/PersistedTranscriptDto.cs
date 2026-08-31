namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// One persisted <c>request_transcripts</c> row as read from the router's
/// <c>TelemetryService.ListPersistedSessions</c> RPC (docs/router/sessions-tab-training-data-plan.md
/// Phase 2), decoded from the generated <c>Contract.PersistedTranscript</c> wire type into a plain DTO -
/// the same "generated type stays at the gRPC boundary" convention <see cref="RoutingTelemetryEventDto"/>
/// follows for the live stream.
/// </summary>
/// <param name="SessionId">The session portion of <paramref name="CorrelationId"/>, computed router-side by <c>CorrelationIdParser.SessionIdOf</c>.</param>
/// <param name="CorrelationId">The full per-request correlation id, <c>"{SessionId}:{turnNumber}"</c> - the turn number is parsed from this by <see cref="PersistedSessionAggregator"/>.</param>
/// <param name="CreatedAtUtc">When this row was written, in UTC.</param>
/// <param name="RequestedModel">The client's literal requested model name.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="PromptText">The captured prompt text, or <see langword="null"/> when unavailable.</param>
/// <param name="ResponseText">The captured response text, or <see langword="null"/> when unavailable.</param>
/// <param name="CostUsd">The estimated dollar cost, or <see langword="null"/> when unknown.</param>
/// <param name="InputTokens">The prompt token count, or <see langword="null"/> when unknown.</param>
/// <param name="OutputTokens">The completion token count, or <see langword="null"/> when unknown.</param>
/// <param name="MemoryEntryId">
/// The linked <c>memory_entries</c> row id, or <see langword="null"/> if this transcript was never folded
/// into the live-learning corpus - the "used for live training" signal the Sessions tab badges.
/// </param>
public sealed record PersistedTranscriptDto(
    string SessionId,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string RequestedModel,
    string RoutedModel,
    string? PromptText,
    string? ResponseText,
    decimal? CostUsd,
    int? InputTokens,
    int? OutputTokens,
    long? MemoryEntryId);
