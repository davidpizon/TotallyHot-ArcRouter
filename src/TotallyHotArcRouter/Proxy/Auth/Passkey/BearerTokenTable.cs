using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// In-memory table keyed by SHA-256 of opaque bearer tokens (ADR-0020). Storing hashes rather than
/// plaintext tokens keeps timing from leaking whether a presented value was ever valid.
/// </summary>
public sealed class BearerTokenTable<TEntry> where TEntry : class
{
    private readonly ConcurrentDictionary<string, TEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="BearerTokenTable{TEntry}"/> class.</summary>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public BearerTokenTable(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the clock used for expiry checks on entries that expose an expiry time.</summary>
    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Inserts the factory-produced entry under a freshly generated 32-byte token and returns the
    /// base64url plaintext for the caller to hand to the dashboard.
    /// </summary>
    public string Issue(Func<string, TEntry> entryFactory)
    {
        ArgumentNullException.ThrowIfNull(entryFactory);
        while (true)
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = PasskeyEncoding.ToBase64Url(tokenBytes);
            var hashKey = HashToken(token);
            var entry = entryFactory(hashKey);
            if (_entries.TryAdd(hashKey, entry)) return token;
        }
    }

    /// <summary>Checks whether <paramref name="token"/> maps to a non-expired entry without removing it.</summary>
    public bool TryPeek(string? token, out TEntry? entry, Func<TEntry, DateTimeOffset> getExpiry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hashKey = HashToken(token);
        if (!_entries.TryGetValue(hashKey, out var stored)) return false;
        if (_timeProvider.GetUtcNow() >= getExpiry(stored))
        {
            _entries.TryRemove(hashKey, out _);
            return false;
        }

        entry = stored;
        return true;
    }

    /// <summary>
    /// Atomically removes and returns the entry for <paramref name="token"/> when still valid. Callers
    /// validate binding after removal so a mismatch still consumes the authorization (ADR-0020).
    /// </summary>
    public bool TryConsume(string? token, out TEntry? entry, Func<TEntry, DateTimeOffset> getExpiry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hashKey = HashToken(token);
        if (!_entries.TryRemove(hashKey, out var stored)) return false;
        if (_timeProvider.GetUtcNow() >= getExpiry(stored)) return false;

        entry = stored;
        return true;
    }

    /// <summary>Removes the entry for <paramref name="token"/> if present.</summary>
    public bool Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return _entries.TryRemove(HashToken(token), out _);
    }

    /// <summary>Removes every entry.</summary>
    public void RevokeAll() => _entries.Clear();

    /// <summary>Hashes a presented token for dictionary lookup.</summary>
    public static string HashToken(string token)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
