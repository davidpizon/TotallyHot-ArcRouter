namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>One turn (routed request) within a live-aggregated conversation.</summary>
public sealed record LiveConversationTurn(
    string SessionId,
    int TurnNumber,
    string Agent,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    decimal EstimatedCostUsd,
    bool IsFallback,
    long LatencyToHeadersMs,
    DateTimeOffset TimestampUtc,
    int CacheCreationTokens = 0,
    int CacheReadTokens = 0,
    string? RequestSummary = null,
    string? ResponseSummary = null,
    string? CostConfidence = null,
    string? RequestedModel = null,
    string? RoutedModel = null,
    string? SubstitutionReason = null);

/// <summary>A conversation (session) reconstructed from the live telemetry stream.</summary>
/// <param name="SessionId">The session id every turn in <paramref name="Turns"/> shares.</param>
/// <param name="IsSessionSynthesized">
/// Whether the session id was synthesized rather than resolved from an explicit
/// client-sent id.
/// </param>
/// <param name="FirstTimestampUtc">The first turn's timestamp.</param>
/// <param name="LastTimestampUtc">The most recent turn's timestamp.</param>
/// <param name="TotalCost">
/// The sum of every turn's known cost - see <paramref name="UnpricedTurns"/> for what this
/// excludes.
/// </param>
/// <param name="TotalPromptTokens">The sum of every turn's prompt tokens.</param>
/// <param name="TotalCompletionTokens">The sum of every turn's completion tokens.</param>
/// <param name="HasFallbackTurns">Whether any turn in <paramref name="Turns"/> was served by fallback routing.</param>
/// <param name="Turns">This conversation's turns, in chronological order.</param>
/// <param name="TotalCacheCreationTokens">The sum of every turn's cache-creation tokens.</param>
/// <param name="TotalCacheReadTokens">The sum of every turn's cache-read tokens.</param>
/// <param name="UnpricedTurns">
/// How many turns in <paramref name="Turns"/> had no cost at all (a null <c>EstimatedCostUsd</c> - see
/// <see cref="RoutingTelemetryEventDto.EstimatedCostUsd"/>), and so contributed nothing to
/// <paramref name="TotalCost"/>: a non-zero value here means <paramref name="TotalCost"/> is a floor, not
/// a total (§5.6, <c>docs/router/token-tracking-improvements.md</c>).
/// </param>
public sealed record LiveConversation(
    string SessionId,
    bool IsSessionSynthesized,
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    decimal TotalCost,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    bool HasFallbackTurns,
    IReadOnlyList<LiveConversationTurn> Turns,
    int TotalCacheCreationTokens = 0,
    int TotalCacheReadTokens = 0,
    int UnpricedTurns = 0);

/// <summary>
/// Groups a flat stream of <see cref="RoutingTelemetryEventDto"/>s into conversations, mirroring the
/// shape <c>Models.Conversation</c>/<c>ConversationTurn</c> expect on the Gui side (see the mapping
/// layer in <c>TotallyHot.ArcRouter.Gui</c> that converts this pure output into those view-model types).
/// Pure and stateless: callers own how/when to re-run it as new events arrive (e.g. re-aggregate the
/// full accumulated event list on every new event - see the "Verification limitation" and
/// "Known gaps" notes on this feature in docs/gui/dashboard.md for why several
/// <c>ConversationTurn</c> fields - RoutingRoi, ToolExecutionSteps,
/// ContextBufferPercent, RoutingSteps - have no live-data source yet and are left at safe defaults by
/// the Gui-side mapping layer, not by this aggregator. RequestSummary/ResponseSummary are real: they
/// pass straight through from <see cref="RoutingTelemetryEventDto"/>, already truncated server-side.
/// CacheHitRate is also real now: the Gui-side mapping layer derives it from
/// <see cref="LiveConversationTurn.CacheCreationTokens"/>/<see cref="LiveConversationTurn.CacheReadTokens"/>.
/// </summary>
public static class ConversationAggregator
{
    /// <summary>
    /// Groups events by <see cref="RoutingTelemetryEventDto.SessionId"/>, orders each conversation's
    /// turns by <see cref="RoutingTelemetryEventDto.TurnNumber"/> (turn numbers are assigned server-side
    /// in arrival order, so this is also chronological order), and orders conversations by most
    /// recently active first.
    /// </summary>
    public static IReadOnlyList<LiveConversation> Aggregate(IReadOnlyList<RoutingTelemetryEventDto> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .GroupBy(keySelector: e => e.SessionId, comparer: StringComparer.Ordinal)
            .Select(BuildConversation)
            .OrderByDescending(c => c.LastTimestampUtc)
            .ToList();
    }

    /// <summary>
    /// Builds one <see cref="LiveConversation"/> from a single session's grouped events, ordering its turns
    /// chronologically.
    /// </summary>
    private static LiveConversation BuildConversation(IGrouping<string, RoutingTelemetryEventDto> group)
    {
        var orderedEvents = group.OrderBy(e => e.TurnNumber).ToList();

        var turns = orderedEvents
            .Select(e => new LiveConversationTurn(
                SessionId: e.SessionId,
                TurnNumber: e.TurnNumber,
                Agent: e.ResolvedModel,
                Model: e.ResolvedModel,
                PromptTokens: e.PromptTokens ?? 0,
                CompletionTokens: e.CompletionTokens ?? 0,
                EstimatedCostUsd: e.EstimatedCostUsd ?? 0m,
                IsFallback: e.IsFallback,
                LatencyToHeadersMs: e.LatencyToHeadersMs,
                TimestampUtc: e.TimestampUtc,
                CacheCreationTokens: e.CacheCreationTokens ?? 0,
                CacheReadTokens: e.CacheReadTokens ?? 0,
                RequestSummary: e.RequestSummary,
                ResponseSummary: e.ResponseSummary,
                CostConfidence: e.CostConfidence,
                RequestedModel: e.RequestedModel,
                RoutedModel: e.RoutedModel,
                SubstitutionReason: e.SubstitutionReason))
            .ToList();

        return new LiveConversation(
            SessionId: group.Key,
            IsSessionSynthesized: orderedEvents[0].IsSessionSynthesized,
            FirstTimestampUtc: orderedEvents[0].TimestampUtc,
            LastTimestampUtc: orderedEvents[^1].TimestampUtc,
            TotalCost: turns.Sum(t => t.EstimatedCostUsd),
            TotalPromptTokens: turns.Sum(t => t.PromptTokens),
            TotalCompletionTokens: turns.Sum(t => t.CompletionTokens),
            HasFallbackTurns: turns.Any(t => t.IsFallback),
            Turns: turns,
            TotalCacheCreationTokens: turns.Sum(t => t.CacheCreationTokens),
            TotalCacheReadTokens: turns.Sum(t => t.CacheReadTokens),
            UnpricedTurns: orderedEvents.Count(e => e.EstimatedCostUsd is null));
    }
}