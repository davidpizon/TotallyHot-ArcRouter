namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// One persisted <c>request_transcripts</c> row as read from the router's
/// <c>TelemetryService.ListPersistedSessions</c> RPC (docs/router/sessions-tab-training-data-plan.md
/// Phase 2), decoded from the generated <c>Contract.PersistedTranscript</c> wire type into a plain DTO -
/// the same "generated type stays at the gRPC boundary" convention <see cref="RoutingTelemetryEventDto"/>
/// follows for the live stream.
/// </summary>
/// <param name="SessionId">
/// The session portion of <paramref name="CorrelationId"/>, computed router-side by
/// <c>CorrelationIdParser.SessionIdOf</c>.
/// </param>
/// <param name="CorrelationId">
/// The full per-request correlation id, <c>"{SessionId}:{turnNumber}"</c> - the turn number is
/// parsed from this by <see cref="PersistedSessionAggregator"/>.
/// </param>
/// <param name="CreatedAtUtc">When this row was written, in UTC.</param>
/// <param name="RequestedModel">The client's literal requested model name.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="PromptText">
/// A display preview of the captured prompt text - at most 2,000 characters plus "…" when cut, the same
/// preview live telemetry carries (#179, ADR-0023) - or <see langword="null"/> when unavailable. Never the
/// full stored text.
/// </param>
/// <param name="ResponseText">
/// A display preview of the captured response text, cut the same way as <paramref name="PromptText"/>, or
/// <see langword="null"/> when unavailable.
/// </param>
/// <param name="CostUsd">The estimated dollar cost, or <see langword="null"/> when unknown.</param>
/// <param name="InputTokens">The prompt token count, or <see langword="null"/> when unknown.</param>
/// <param name="OutputTokens">The completion token count, or <see langword="null"/> when unknown.</param>
/// <param name="MemoryEntryId">
/// The linked <c>memory_entries</c> row id, or <see langword="null"/> if this transcript was never folded
/// into the live-learning corpus - the "used for live training" signal the Sessions tab badges.
/// </param>
/// <param name="TranscriptId">
/// The router's <c>request_transcripts.id</c> for this row - the key a per-turn text read takes to load more
/// than the preview. 0 when the router didn't send it.
/// </param>
/// <param name="PromptTextLength">
/// The stored prompt text's length in characters, as the router's SQLite counts them, or
/// <see langword="null"/> when unknown. For display only: <paramref name="PromptTruncated"/> says whether
/// the preview was cut.
/// </param>
/// <param name="ResponseTextLength">The stored response text's length, counted like <paramref name="PromptTextLength"/>.</param>
/// <param name="PromptTruncated">Whether <paramref name="PromptText"/> is a cut preview of a longer stored text.</param>
/// <param name="ResponseTruncated">Whether <paramref name="ResponseText"/> is a cut preview of a longer stored text.</param>
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
    long? MemoryEntryId,
    long TranscriptId = 0,
    int? PromptTextLength = null,
    int? ResponseTextLength = null,
    bool PromptTruncated = false,
    bool ResponseTruncated = false);