using Serilog;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Global bounded store of pending WebAuthn challenges (ADR-0020). Challenges expire after two minutes,
/// at most 32 may be outstanding, and issuance is gated by a token bucket so a loopback flood cannot
/// exhaust CPU or log space without limit.
/// </summary>
public sealed class ChallengeStore
{
    /// <summary>Maximum pending challenges before the oldest is evicted.</summary>
    public const int MaxPending = 32;

    /// <summary>How long a issued challenge remains valid.</summary>
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, PendingChallenge> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly Lock _bucketLock = new();
    private readonly int _tokensPerMinute;
    private readonly int _burst;
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private DateTimeOffset _refusalWindowStart;
    private int _refusalsInWindow;

    /// <summary>Initializes a new instance of the <see cref="ChallengeStore"/> class.</summary>
    /// <param name="options">Supplies issuance rate limits.</param>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public ChallengeStore(PasskeyOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tokensPerMinute = Math.Max(1, options.ChallengeIssuancePerMinute);
        _burst = Math.Max(1, options.ChallengeIssuanceBurst);
        _tokens = _burst;
        _lastRefill = _timeProvider.GetUtcNow();
        _refusalWindowStart = _lastRefill;
    }

    /// <summary>
    /// Issues a fresh 32-byte challenge optionally bound to a one-operation authorization target. Throws
    /// <see cref="PasskeyGateException"/> with <see cref="Grpc.Core.StatusCode.ResourceExhausted"/> when
    /// the token bucket is empty.
    /// </summary>
    /// <param name="operation">Optional gated operation name for assertions.</param>
    /// <param name="parameters">Optional parameters string bound to the operation.</param>
    /// <returns>The challenge bytes embedded in WebAuthn options.</returns>
    public byte[] Issue(string? operation = null, string? parameters = null)
    {
        if (!TryTakeToken())
        {
            CoalesceRefusalLog();
            throw new PasskeyGateException(
                statusCode: Grpc.Core.StatusCode.ResourceExhausted,
                detail: "Passkey challenge issuance rate exceeded.");
        }

        EvictExpired();
        if (_pending.Count >= MaxPending) EvictOldest();

        var challenge = RandomNumberGenerator.GetBytes(32);
        var key = PasskeyEncoding.ToBase64Url(challenge);
        var entry = new PendingChallenge(
            Challenge: challenge,
            Operation: operation,
            Parameters: parameters ?? string.Empty,
            ExpiresAtUtc: _timeProvider.GetUtcNow().Add(ChallengeTtl));
        _pending[key] = entry;
        return challenge;
    }

    /// <summary>
    /// Removes and returns the pending challenge matching <paramref name="challenge"/> if still valid.
    /// Consumption happens before verification so every attempt burns the challenge (ADR-0020).
    /// </summary>
    public bool TryConsume(ReadOnlySpan<byte> challenge, out PendingChallenge? pending)
    {
        pending = null;
        var key = PasskeyEncoding.ToBase64Url(challenge);
        if (!_pending.TryRemove(key, out var entry)) return false;

        if (_timeProvider.GetUtcNow() >= entry.ExpiresAtUtc) return false;

        pending = entry;
        return true;
    }

    /// <summary>Pending challenge metadata stored until consumed or evicted.</summary>
    /// <param name="Challenge">The raw challenge bytes.</param>
    /// <param name="Operation">Bound operation, if any.</param>
    /// <param name="Parameters">Bound parameters string.</param>
    /// <param name="ExpiresAtUtc">UTC expiry.</param>
    public sealed record PendingChallenge(
        byte[] Challenge,
        string? Operation,
        string Parameters,
        DateTimeOffset ExpiresAtUtc);

    private bool TryTakeToken()
    {
        lock (_bucketLock)
        {
            RefillTokens();
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }

    private void RefillTokens()
    {
        var now = _timeProvider.GetUtcNow();
        var elapsed = now - _lastRefill;
        if (elapsed <= TimeSpan.Zero) return;

        var refill = elapsed.TotalMinutes * _tokensPerMinute;
        _tokens = Math.Min(_burst, _tokens + refill);
        _lastRefill = now;
    }

    private void EvictExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, entry) in _pending)
        {
            if (now >= entry.ExpiresAtUtc) _pending.TryRemove(key, out _);
        }
    }

    private void EvictOldest()
    {
        var oldest = _pending.OrderBy(p => p.Value.ExpiresAtUtc).Select(p => p.Key).FirstOrDefault();
        if (oldest is not null) _pending.TryRemove(oldest, out _);
    }

    private void CoalesceRefusalLog()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_bucketLock)
        {
            if (now - _refusalWindowStart >= TimeSpan.FromMinutes(1))
            {
                if (_refusalsInWindow > 0)
                {
                    Log.Warning(
                        "Passkey challenge issuance refused {Count} times in the last minute",
                        _refusalsInWindow);
                }

                _refusalWindowStart = now;
                _refusalsInWindow = 0;
            }

            _refusalsInWindow++;
        }
    }
}
