namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// <see cref="IConversationTurnTracker"/> backed by <see cref="IUsageLedger"/>
/// (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2, §5.5). On first sight of a session
/// this process hasn't tracked yet, seeds the in-memory counter from the ledger's highest recorded turn
/// number for that session, then counts purely in memory from there - so a turn number keeps advancing
/// across a proxy restart instead of resetting to 1, without a ledger round-trip on every call.
/// </summary>
/// <remarks>
/// Idle sessions are evicted after <see cref="IdleEviction"/> to keep memory bounded on a long-running
/// process, exactly like <see cref="ConversationTurnTracker"/>'s in-memory counterpart and
/// <see cref="MessageHistoryContinuityMatcher"/>'s tracked-conversation list. Eviction is safe here
/// specifically because of the seeding: a session that goes idle long enough to be evicted and then
/// resumes simply re-seeds from the ledger on its next call, picking up where the ledger left off rather
/// than restarting at 1.
/// </remarks>
public sealed class PersistentConversationTurnTracker : IConversationTurnTracker
{
    private static readonly TimeSpan IdleEviction = TimeSpan.FromHours(12);

    private readonly object _lock = new();
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);
    private readonly IUsageLedger _ledger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentConversationTurnTracker"/> class.
    /// </summary>
    /// <param name="ledger">The durable ledger seeding each session's counter on first sight.</param>
    /// <param name="timeProvider">Optional; defaults to <see cref="TimeProvider.System"/>. Overridable in tests to control idle eviction.</param>
    public PersistentConversationTurnTracker(IUsageLedger ledger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public int NextTurn(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id must not be null or whitespace.", nameof(sessionId));
        }

        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            EvictIdle(now);

            var counter = _sessions.TryGetValue(sessionId, out var tracked)
                ? tracked.Counter
                : _ledger.GetMaxTurnNumber(sessionId);

            var next = counter + 1;
            _sessions[sessionId] = new TrackedSession(next, now);
            return next;
        }
    }

    // Called under _lock. Walks the whole map on every call rather than a scheduled sweep: session counts
    // are small relative to request volume, and this keeps the tracker free of any background timer to
    // dispose of.
    private void EvictIdle(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (var (sessionId, tracked) in _sessions)
        {
            if (now - tracked.LastSeenUtc > IdleEviction)
            {
                (stale ??= []).Add(sessionId);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var sessionId in stale)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>One session's in-memory turn counter and when it was last advanced, for idle eviction.</summary>
    private readonly record struct TrackedSession(int Counter, DateTimeOffset LastSeenUtc);
}
