using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Holds one session's persisted turn details for the Sessions tabs (#176 phase 2). The host registers it
/// when a persisted-sessions client exists. A component that renders without it shows the loaded turn only.
/// </summary>
public sealed class SessionDetailsStore
{
    private readonly IPersistedSessionsClient _client;
    private readonly Dictionary<int, SessionTurnDetailDto> _byTurn = [];
    private int _generation;

    /// <summary>Initializes a new instance of the <see cref="SessionDetailsStore"/> class.</summary>
    /// <param name="client">The metadata client. Text stays on <c>GetTurnTexts</c>.</param>
    public SessionDetailsStore(IPersistedSessionsClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>The session these details belong to, or null before the first load.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The first page's retained-history aggregate, or null.</summary>
    public SessionAggregateDto? Aggregate { get; private set; }

    /// <summary>Whether the last load found capture switched off.</summary>
    public bool CaptureOff { get; private set; }

    /// <summary>The last load error, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The cursor for the next older page, or null when the loaded pages are the end.</summary>
    private long? NextBeforeTranscriptId { get; set; }

    /// <summary>The detail for <paramref name="turnNumber"/>, or null when this page does not have it.</summary>
    /// <param name="turnNumber">The turn inside the open session.</param>
    public SessionTurnDetailDto? DetailFor(int turnNumber) => _byTurn.GetValueOrDefault(turnNumber);

    /// <summary>Loads the newest page and replaces whatever this store was showing.</summary>
    /// <param name="sessionId">The session the operator opened.</param>
    /// <param name="liveFloorTurnNumber">The lowest turn number the GUI holds live.</param>
    /// <param name="cancellationToken">Cancels the load. A newer load also ignores this result.</param>
    public async Task LoadAsync(string sessionId, int liveFloorTurnNumber, CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var page = await _client.GetSessionTurnDetailsAsync(
                sessionId, 500, null, liveFloorTurnNumber, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation)) return;
            _byTurn.Clear();
            foreach (var turn in page.Turns) _byTurn[turn.TurnNumber] = turn;
            SessionId = sessionId;
            Aggregate = page.Aggregate;
            CaptureOff = !page.TranscriptCaptureEnabled;
            NextBeforeTranscriptId = page.NextBeforeTranscriptId;
            Error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Error = "Persisted details couldn't be loaded.";
        }
    }

    /// <summary>Appends the next older page. Does nothing when the newest page is still the end.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async Task LoadEarlierAsync(CancellationToken cancellationToken = default)
    {
        if (SessionId is null || NextBeforeTranscriptId is null) return;
        var generation = Volatile.Read(ref _generation);
        var sessionId = SessionId;
        var before = NextBeforeTranscriptId.Value;
        try
        {
            var page = await _client.GetSessionTurnDetailsAsync(sessionId, 500, before, 0, cancellationToken)
                .ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation) || !string.Equals(SessionId, sessionId, StringComparison.Ordinal))
                return;
            foreach (var turn in page.Turns) _byTurn[turn.TurnNumber] = turn;
            NextBeforeTranscriptId = page.NextBeforeTranscriptId;
            Error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Error = "Older turns couldn't be loaded.";
        }
    }
}
