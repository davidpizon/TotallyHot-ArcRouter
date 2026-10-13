using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// <see cref="IConversationTurnTracker"/> backed by <see cref="IUsageLedger"/> and, when available,
/// <see cref="ITranscriptStore"/> (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2, §5.5;
/// #176 §5.7). On first sight of a session this process hasn't tracked yet, seeds the in-memory counter from
/// the higher of the ledger's and the transcript store's highest recorded turn number for that session, then
/// counts purely in memory from there - so a turn number keeps advancing across a proxy restart instead of
/// resetting to 1, without a ledger or transcript round-trip on every call.
/// </summary>
/// <remarks>
/// Idle sessions are evicted after <see cref="IdleEviction"/> to keep memory bounded on a long-running
/// process, exactly like <see cref="ConversationTurnTracker"/>'s in-memory counterpart and
/// <see cref="MessageHistoryContinuityMatcher"/>'s tracked-conversation list. Eviction is safe here
/// specifically because of the seeding: a session that goes idle long enough to be evicted and then
/// resumes simply re-seeds from both stores on its next call, picking up where the durable records left
/// off rather than restarting at 1. Seeding from transcripts as well as the ledger closes the gap where a
/// turn left a <c>request_transcripts</c> row but no ledger row (usage extraction failed or was skipped),
/// which would otherwise reissue that turn number after a restart and break the Sessions tab's per-turn merge.
/// </remarks>
public sealed class PersistentConversationTurnTracker : IConversationTurnTracker
{
    private static readonly TimeSpan IdleEviction = TimeSpan.FromHours(12);
    private readonly IUsageLedger _ledger;
    private readonly ITranscriptStore? _transcripts;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentConversationTurnTracker"/> class.
    /// </summary>
    /// <param name="ledger">The durable ledger seeding each session's counter on first sight.</param>
    /// <param name="transcripts">
    /// Optional transcript store whose highest turn number is also considered when seeding. Null keeps the
    /// pre-#176 ledger-only seed so hand-built trackers in tests need no transcript double. The singleton
    /// registration resolves this when an <see cref="ITranscriptStore"/> is present.
    /// </param>
    /// <param name="timeProvider">
    /// Optional; defaults to <see cref="TimeProvider.System"/>. Overridable in tests to control
    /// idle eviction.
    /// </param>
    public PersistentConversationTurnTracker(
        IUsageLedger ledger,
        ITranscriptStore? transcripts = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
        _transcripts = transcripts;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public int NextTurn(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException(message: "Session id must not be null or whitespace.",
                paramName: nameof(sessionId));

        var now = _timeProvider.GetUtcNow();

        // Fast path: an already-tracked, still-fresh session skips both the O(n) eviction scan and any
        // ledger/transcript round-trip - the common case on this hot path (ProxyMiddleware calls NextTurn for
        // every routed request), since most requests are turn >1 of an already-seen session. An idle-expired
        // entry falls through to the slow path below, which evicts it and re-seeds exactly as before.
        lock (_lock)
        {
            if (_sessions.TryGetValue(key: sessionId, value: out var tracked) &&
                now - tracked.LastSeenUtc <= IdleEviction)
            {
                var next = tracked.Counter + 1;
                _sessions[sessionId] = new TrackedSession(Counter: next, LastSeenUtc: now);
                return next;
            }
        }

        // Not tracked (or idle-expired): both GetMaxTurnNumber calls are synchronous SQLite queries, so they
        // run outside _lock rather than serializing every other session's NextTurn call behind this one's I/O.
        var seed = Math.Max(
            _ledger.GetMaxTurnNumber(sessionId),
            _transcripts?.GetMaxTurnNumber(sessionId) ?? 0);

        lock (_lock)
        {
            EvictIdle(now);

            // Re-check under the lock: another thread may have raced this one and already tracked/advanced
            // this session while the queries above were in flight - use its counter rather than
            // silently reissuing an already-issued turn number.
            var counter = _sessions.TryGetValue(key: sessionId, value: out var tracked) ? tracked.Counter : seed;
            var next = counter + 1;
            _sessions[sessionId] = new TrackedSession(Counter: next, LastSeenUtc: now);
            return next;
        }
    }

    /// <summary>
    /// Removes every session whose <see cref="TrackedSession.LastSeenUtc"/> is older than
    /// <see cref="IdleEviction"/> from <see cref="_sessions"/>.
    /// </summary>
    /// <param name="now">The current time, used as the eviction reference point.</param>
    // Called under _lock, on NextTurn's slow path only (an untracked or idle-expired session) - not on
    // every call, since the fast path above returns before reaching this. Walks the whole map rather than
    // a scheduled sweep: session counts are small relative to request volume, and this keeps the tracker
    // free of any background timer to dispose of. A stretch of pure fast-path traffic (all sessions already
    // tracked and fresh) won't itself trigger a sweep of other, unrelated idle sessions - they're cleaned up
    // the next time any session takes the slow path, which is frequent enough in practice (every new or
    // idle-expired session) to keep memory bounded per this type's own idle-eviction contract.
    private void EvictIdle(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (var (sessionId, tracked) in _sessions)
            if (now - tracked.LastSeenUtc > IdleEviction)
                (stale ??= []).Add(sessionId);

        if (stale is null) return;

        foreach (var sessionId in stale) _sessions.Remove(sessionId);
    }

    /// <summary>One session's in-memory turn counter and when it was last advanced, for idle eviction.</summary>
    private readonly record struct TrackedSession(int Counter, DateTimeOffset LastSeenUtc);
}
