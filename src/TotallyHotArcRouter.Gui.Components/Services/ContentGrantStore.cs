namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Holds the dashboard's content grant (ADR-0020): the short-lived bearer token a passkey unlock earns, which
/// the router requires before it lets conversation text out. A singleton so every client built over the shared
/// call invoker, and the live telemetry stream, sees the same grant; the grant lives in this object's memory
/// and nowhere else - never in <c>localStorage</c>, <c>sessionStorage</c>, or a cookie - so a page reload is a
/// lock.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Clear"/> is the single "lock" path: the explicit Lock control, the expiry timer, a content RPC the
/// router rejected for the grant, and a telemetry stream that had to reconnect all end there. It raises
/// <see cref="Cleared"/>, which the conversation stores subscribe to in order to drop every cached prompt and
/// response, so no code path can lock the token yet leave the text on screen. The subscription runs
/// store-to-grant (the stores take this object, not the reverse) so there is no construction cycle.
/// </para>
/// <para>
/// Events are raised on whichever thread called <see cref="SetGrant"/> or <see cref="Clear"/> - for the expiry
/// timer, a thread-pool thread - so subscribers that touch UI state must marshal back to their own context.
/// </para>
/// </remarks>
public sealed class ContentGrantStore : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private string? _token;
    private DateTimeOffset? _expiresAtUtc;
    private ITimer? _expiryTimer;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ContentGrantStore"/> class.</summary>
    /// <param name="timeProvider">The clock and timer source; defaults to <see cref="TimeProvider.System"/>. Tests pass a fake.</param>
    public ContentGrantStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after the grant was set or cleared.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised after a new grant was stored. The live telemetry stream restarts on this, because a gRPC stream
    /// carries the metadata it was opened with and would otherwise keep running without the new grant.
    /// </summary>
    public event Action? Granted;

    /// <summary>
    /// Raised after a held grant was dropped. Subscribers clear every cached conversation text field; metadata
    /// stays. Not raised by <see cref="Clear"/> when no grant was held, so a redundant lock does not churn the UI.
    /// </summary>
    public event Action? Cleared;

    /// <summary>
    /// Gets the grant token to put in the <c>x-content-grant</c> header, or <see langword="null"/> when none is
    /// held or it has lapsed. Reading an expired grant returns <see langword="null"/> even if the expiry timer
    /// has not fired yet, so a call racing the timer never sends a token the router will reject.
    /// </summary>
    public string? Token
    {
        get
        {
            lock (_lock)
            {
                return IsActiveCore() ? _token : null;
            }
        }
    }

    /// <summary>Gets when the held grant lapses, or <see langword="null"/> when none is held.</summary>
    public DateTimeOffset? ExpiresAtUtc
    {
        get
        {
            lock (_lock)
            {
                return IsActiveCore() ? _expiresAtUtc : null;
            }
        }
    }

    /// <summary>Gets whether a grant is held and has not yet expired.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return IsActiveCore();
            }
        }
    }

    /// <summary>
    /// Stores a freshly issued grant, replacing any previous one, and arms a timer that calls
    /// <see cref="Clear"/> when it lapses. A grant whose expiry is already past is treated as a lock rather
    /// than stored.
    /// </summary>
    /// <param name="token">The opaque bearer token from <c>FinishContentUnlock</c>.</param>
    /// <param name="expiresAtUtc">When the router stops honoring <paramref name="token"/>.</param>
    public void SetGrant(string token, DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var remaining = expiresAtUtc - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            Clear();
            return;
        }

        lock (_lock)
        {
            if (_disposed) return;

            _expiryTimer?.Dispose();
            _token = token;
            _expiresAtUtc = expiresAtUtc;
            _expiryTimer = _timeProvider.CreateTimer(callback: _ => ClearIfStillExpiring(expiresAtUtc), state: null,
                dueTime: remaining, period: Timeout.InfiniteTimeSpan);
        }

        Granted?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>
    /// The expiry timer's callback: clears the grant only if it is still the one the timer was armed for, so
    /// a callback already in flight for a replaced grant cannot lock out the newer one.
    /// </summary>
    private void ClearIfStillExpiring(DateTimeOffset armedFor)
    {
        lock (_lock)
        {
            if (_expiresAtUtc != armedFor) return;
        }

        Clear();
    }

    /// <summary>
    /// Drops the held grant and tells subscribers to drop every cached conversation text field. Safe to call
    /// repeatedly and from any thread. This clears only the local copy; the router-side revocation is the
    /// separate <c>LockContent</c> RPC, which the caller issues first when the operator asked to lock.
    /// </summary>
    public void Clear()
    {
        bool hadGrant;
        lock (_lock)
        {
            hadGrant = _token is not null;
            _token = null;
            _expiresAtUtc = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }

        if (!hadGrant) return;

        Cleared?.Invoke();
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _token = null;
            _expiresAtUtc = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
    }

    /// <summary>Whether a grant is held and unexpired. The caller must hold <see cref="_lock"/>.</summary>
    private bool IsActiveCore()
    {
        return _token is not null && _expiresAtUtc is { } expires && expires > _timeProvider.GetUtcNow();
    }
}
