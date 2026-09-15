using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Proxy.Auth;

/// <summary>
/// Throttles <c>POST /auth/login</c> attempts per remote address (ADR-0012's "rate-limited" requirement
/// for the token-login fallback) with a fixed-window counter: at most <see cref="MaxAttempts"/> failed
/// attempts per <see cref="Window"/>, per address. A successful login clears that address's counter
/// immediately, so a legitimate caller who mistyped the token once is never penalized for it.
/// </summary>
/// <remarks>
/// Deliberately in-memory and per-process, matching every other piece of Phase P4 session state (the
/// HMAC key, the rotation generation): the login surface being briefly more permissive right after a
/// restart is an acceptable trade for not needing a persistence layer for a throttling counter.
/// </remarks>
public sealed class LoginRateLimiter
{
    /// <summary>The number of failed attempts allowed within <see cref="Window"/> before <see cref="ShouldThrottle"/> returns <see langword="true"/>.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The sliding window <see cref="MaxAttempts"/> is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="LoginRateLimiter"/> class.</summary>
    /// <param name="timeProvider">The clock to use. Defaults to <see cref="TimeProvider.System"/>; overridable in tests.</param>
    public LoginRateLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns whether <paramref name="key"/> (typically the caller's remote IP) has already exhausted
    /// its attempt budget for the current window, without consuming an attempt.
    /// </summary>
    public bool ShouldThrottle(string key)
    {
        if (!_buckets.TryGetValue(key, out var bucket)) return false;

        var now = _timeProvider.GetUtcNow();
        lock (bucket)
        {
            return bucket.Count >= MaxAttempts && now - bucket.WindowStart < Window;
        }
    }

    /// <summary>Records a failed attempt for <paramref name="key"/>, starting a new window if the previous one expired.</summary>
    public void RecordFailure(string key)
    {
        var now = _timeProvider.GetUtcNow();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(now));
        lock (bucket)
        {
            if (now - bucket.WindowStart >= Window)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            bucket.Count++;
        }
    }

    /// <summary>Clears <paramref name="key"/>'s counter after a successful login.</summary>
    public void RecordSuccess(string key)
    {
        _buckets.TryRemove(key, out _);
    }

    private sealed class Bucket(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart = windowStart;
        public int Count;
    }
}
