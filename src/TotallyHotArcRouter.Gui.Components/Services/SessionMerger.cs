using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Merges live and persisted Sessions-tab conversations per turn (#176 §5.1). Replaces the previous
/// per-session "live wins and drops every persisted turn" rule so a GUI restart that then sees one new
/// live turn still shows the earlier persisted history for that session.
/// </summary>
public static class SessionMerger
{
    /// <summary>
    /// Unions <paramref name="live"/> and <paramref name="persisted"/> by session id. A session found in
    /// only one source passes through unchanged. A session found in both becomes one conversation whose
    /// turns are the union keyed by turn number; when both sides have the same number, the live turn wins
    /// for metrics and its text is replaced by the persisted text when the live summary is truncated or
    /// missing.
    /// </summary>
    /// <param name="live">Conversations currently in the live telemetry buffer.</param>
    /// <param name="persisted">Conversations loaded from persisted transcript history.</param>
    /// <returns>The merged list, most recently active first by newest turn timestamp.</returns>
    public static IReadOnlyList<Conversation> Merge(
        IReadOnlyList<Conversation> live, IReadOnlyList<Conversation> persisted)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(persisted);

        if (persisted.Count == 0) return live;
        if (live.Count == 0) return persisted;

        var liveById = live.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var persistedById = persisted.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var ids = liveById.Keys.Union(persistedById.Keys, StringComparer.Ordinal);

        var merged = new List<Conversation>();
        foreach (var id in ids)
        {
            var hasLive = liveById.TryGetValue(id, out var liveConversation);
            var hasPersisted = persistedById.TryGetValue(id, out var persistedConversation);

            if (hasLive && !hasPersisted)
            {
                merged.Add(liveConversation!);
                continue;
            }

            if (!hasLive && hasPersisted)
            {
                merged.Add(persistedConversation!);
                continue;
            }

            merged.Add(MergeSameSession(liveConversation!, persistedConversation!));
        }

        return [.. merged.OrderByDescending(NewestTurnUtc)];
    }

    /// <summary>Unions turns of one session that exists on both the live and persisted sides.</summary>
    private static Conversation MergeSameSession(Conversation live, Conversation persisted)
    {
        var liveByTurn = ByTurnNumber(live.Turns);
        var persistedByTurn = ByTurnNumber(persisted.Turns);
        var turnNumbers = liveByTurn.Keys.Union(persistedByTurn.Keys).OrderBy(n => n);

        var turns = new List<ConversationTurn>();
        foreach (var number in turnNumbers)
        {
            var hasLive = liveByTurn.TryGetValue(number, out var liveTurn);
            var hasPersisted = persistedByTurn.TryGetValue(number, out var persistedTurn);

            if (hasLive && hasPersisted)
                turns.Add(PreferLiveTextFromPersisted(liveTurn!, persistedTurn!));
            else if (hasLive)
                turns.Add(liveTurn!);
            else
                turns.Add(persistedTurn!);
        }

        var totalCost = turns.Sum(t => t.TotalCost ?? 0m);
        var unpriced = turns.Count(t => t.TotalCost is null);
        var liveTurns = turns.Where(t => t.IsLive).ToList();

        return live with
        {
            FirstTimestamp = turns.Count == 0 ? live.FirstTimestamp : turns[0].Timestamp,
            LastTimestamp = turns.Count == 0 ? live.LastTimestamp : turns[^1].Timestamp,
            TotalCost = totalCost,
            TotalPromptTokens = turns.Sum(t => t.PromptTokens ?? 0),
            TotalCompletionTokens = turns.Sum(t => t.CompletionTokens ?? 0),
            HasFallbackTurns = liveTurns.Any(t => t.IsFallback),
            Turns = turns,
            UnpricedTurns = unpriced,
            IsUsedForTraining = live.IsUsedForTraining || persisted.IsUsedForTraining,
            IsLive = true,
            IsSessionSynthesized = live.IsSessionSynthesized
        };
    }

    /// <summary>
    /// Indexes <paramref name="turns"/> by turn number. A turn number reissued after a restart (before the
    /// turn tracker also seeded from transcripts) can appear twice in one session; the later turn wins
    /// rather than throwing, since a duplicate key here would fail the whole Sessions tab render.
    /// </summary>
    private static Dictionary<int, ConversationTurn> ByTurnNumber(IEnumerable<ConversationTurn> turns)
    {
        var byNumber = new Dictionary<int, ConversationTurn>();
        foreach (var turn in turns) byNumber[turn.TurnNumber] = turn;
        return byNumber;
    }

    /// <summary>
    /// Keeps live metrics and <see cref="ConversationTurn.IsLive"/>, but swaps in persisted prompt/response
    /// text when the live summary is missing or marked truncated (ends with the server's ellipsis marker).
    /// </summary>
    private static ConversationTurn PreferLiveTextFromPersisted(ConversationTurn live, ConversationTurn persisted)
    {
        var request = PreferText(live.RequestSummary, persisted.RequestSummary);
        var response = PreferText(live.ResponseSummary, persisted.ResponseSummary);
        var transcriptId = live.TranscriptId != 0 ? live.TranscriptId : persisted.TranscriptId;

        return live with
        {
            RequestSummary = request,
            ResponseSummary = response,
            TranscriptId = transcriptId,
            IsLive = true
        };
    }

    /// <summary>Chooses persisted text when live text is absent or truncated at the 2,000-character marker.</summary>
    private static string? PreferText(string? live, string? persisted)
    {
        if (string.IsNullOrEmpty(live)) return persisted ?? live;
        if (IsTruncatedLiveSummary(live) && !string.IsNullOrEmpty(persisted)) return persisted;
        return live;
    }

    /// <summary>
    /// The live stream's summary cap (<c>TextTruncator.DefaultMaxLength</c> on the router): longer text is cut
    /// to this many characters and a single "…" marker is appended.
    /// </summary>
    private const int LiveSummaryMaxLength = 2000;

    /// <summary>
    /// Whether <paramref name="text"/> is a live summary the router truncated (see telemetry.md): exactly the
    /// cap plus the trailing "…" marker. Checking the length as well as the marker keeps untruncated text that
    /// merely ends in an ellipsis, and persisted full text longer than the cap, from being flagged.
    /// </summary>
    /// <param name="text">A live prompt or response summary.</param>
    /// <returns><see langword="true"/> when the router cut <paramref name="text"/> at the cap.</returns>
    public static bool IsTruncatedLiveSummary(string? text) =>
        text is { Length: LiveSummaryMaxLength + 1 } && text[^1] == '…';

    /// <summary>The instant of <paramref name="conversation"/>'s newest turn, or the minimum when it has none.</summary>
    private static DateTimeOffset NewestTurnUtc(Conversation conversation) =>
        conversation.Turns.Count == 0
            ? DateTimeOffset.MinValue
            : conversation.Turns.Max(t => t.TimestampUtc);
}
