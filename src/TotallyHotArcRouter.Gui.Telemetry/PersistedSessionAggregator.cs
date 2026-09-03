using System.Globalization;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>One transcript row within a session reconstructed from persisted history.</summary>
/// <param name="CorrelationId">The row's full correlation id, <c>"{SessionId}:{turnNumber}"</c>.</param>
/// <param name="TurnNumber">
/// Parsed from the suffix of <paramref name="CorrelationId"/>; see
/// <see cref="PersistedSessionAggregator.TurnNumberOf"/>.
/// </param>
/// <param name="RequestedModel">The client's literal requested model name.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="PromptText">The captured prompt text, or <see langword="null"/> when unavailable.</param>
/// <param name="ResponseText">The captured response text, or <see langword="null"/> when unavailable.</param>
/// <param name="CostUsd">The estimated dollar cost, or <see langword="null"/> when unknown.</param>
/// <param name="InputTokens">The prompt token count, or <see langword="null"/> when unknown.</param>
/// <param name="OutputTokens">The completion token count, or <see langword="null"/> when unknown.</param>
/// <param name="TimestampUtc">When this row was written, in UTC.</param>
/// <param name="MemoryEntryId">
/// The linked <c>memory_entries</c> row id, or <see langword="null"/> if never folded into the
/// live-learning corpus.
/// </param>
public sealed record PersistedConversationTurn(
    string CorrelationId,
    int TurnNumber,
    string RequestedModel,
    string RoutedModel,
    string? PromptText,
    string? ResponseText,
    decimal? CostUsd,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset TimestampUtc,
    long? MemoryEntryId);

/// <summary>A conversation (session) reconstructed from persisted <c>request_transcripts</c> history.</summary>
/// <param name="SessionId">The session id every turn in <paramref name="Turns"/> shares.</param>
/// <param name="FirstTimestampUtc">The earliest turn's timestamp.</param>
/// <param name="LastTimestampUtc">The most recent turn's timestamp.</param>
/// <param name="TotalCostUsd">The sum of every turn's known cost.</param>
/// <param name="TotalInputTokens">The sum of every turn's known input tokens.</param>
/// <param name="TotalOutputTokens">The sum of every turn's known output tokens.</param>
/// <param name="Turns">This session's turns, oldest first.</param>
/// <param name="IsUsedForTraining">
/// Whether any turn's transcript was folded into the live-learning corpus (
/// <see cref="PersistedConversationTurn.MemoryEntryId"/> set).
/// </param>
public sealed record PersistedConversation(
    string SessionId,
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    decimal TotalCostUsd,
    int TotalInputTokens,
    int TotalOutputTokens,
    IReadOnlyList<PersistedConversationTurn> Turns,
    bool IsUsedForTraining);

/// <summary>
/// Groups a flat list of <see cref="PersistedTranscriptDto"/> rows (as returned by
/// <c>TelemetryService.ListPersistedSessions</c>) into <see cref="PersistedConversation"/>s, mirroring
/// <see cref="ConversationAggregator"/>'s grouping of the live telemetry stream
/// (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
public static class PersistedSessionAggregator
{
    /// <summary>
    /// Groups rows by <see cref="PersistedTranscriptDto.SessionId"/>, orders each session's turns by the
    /// turn number parsed from <see cref="PersistedTranscriptDto.CorrelationId"/>, and orders sessions by
    /// most recently active first - the store's <c>ORDER BY id DESC LIMIT</c> already caps this to the
    /// most recent rows, so a session's turns can arrive out of the id-descending order this re-sorts.
    /// </summary>
    public static IReadOnlyList<PersistedConversation> Aggregate(IReadOnlyList<PersistedTranscriptDto> transcripts)
    {
        ArgumentNullException.ThrowIfNull(transcripts);

        return transcripts
            .GroupBy(keySelector: t => t.SessionId, comparer: StringComparer.Ordinal)
            .Select(BuildConversation)
            .OrderByDescending(c => c.LastTimestampUtc)
            .ToList();
    }

    /// <summary>
    /// Builds one <see cref="PersistedConversation"/> from a single session's grouped rows, ordering its turns by
    /// parsed turn number.
    /// </summary>
    private static PersistedConversation BuildConversation(IGrouping<string, PersistedTranscriptDto> group)
    {
        var turns = group
            .Select(t => new PersistedConversationTurn(
                CorrelationId: t.CorrelationId,
                TurnNumber: TurnNumberOf(t.CorrelationId),
                RequestedModel: t.RequestedModel,
                RoutedModel: t.RoutedModel,
                PromptText: t.PromptText,
                ResponseText: t.ResponseText,
                CostUsd: t.CostUsd,
                InputTokens: t.InputTokens,
                OutputTokens: t.OutputTokens,
                TimestampUtc: t.CreatedAtUtc,
                MemoryEntryId: t.MemoryEntryId))
            .OrderBy(t => t.TurnNumber)
            .ToList();

        return new PersistedConversation(
            SessionId: group.Key,
            FirstTimestampUtc: turns[0].TimestampUtc,
            LastTimestampUtc: turns[^1].TimestampUtc,
            TotalCostUsd: turns.Sum(t => t.CostUsd ?? 0m),
            TotalInputTokens: turns.Sum(t => t.InputTokens ?? 0),
            TotalOutputTokens: turns.Sum(t => t.OutputTokens ?? 0),
            Turns: turns,
            IsUsedForTraining: turns.Any(t => t.MemoryEntryId is not null));
    }

    /// <summary>
    /// Parses the turn number from the suffix of a correlation id composed as
    /// <c>"{sessionId}:{turnNumber}"</c> (<c>ProxyMiddleware</c>'s convention - the same one
    /// <c>CorrelationIdParser.SessionIdOf</c> parses the prefix from, router-side). Falls back to 1 for a
    /// correlation id with no turn suffix or a non-numeric one, so a malformed row still renders in some
    /// stable order rather than throwing.
    /// </summary>
    private static int TurnNumberOf(string correlationId)
    {
        var lastSeparator = correlationId.LastIndexOf(':');
        if (lastSeparator < 0 || lastSeparator == correlationId.Length - 1) return 1;

        var suffix = correlationId[(lastSeparator + 1)..];
        return int.TryParse(s: suffix, provider: CultureInfo.InvariantCulture, result: out var turnNumber)
            ? turnNumber
            : 1;
    }
}