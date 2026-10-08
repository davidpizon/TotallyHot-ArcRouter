namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Issues and validates short-lived content grants after a successful content-unlock WebAuthn ceremony
/// (ADR-0020). Grants authorize read-only conversation text RPCs via the <c>x-content-grant</c> header.
/// </summary>
public sealed class ContentGrantTable
{
    private readonly BearerTokenTable<GrantEntry> _table;
    private readonly PasskeyOptions _options;

    /// <summary>Initializes a new instance of the <see cref="ContentGrantTable"/> class.</summary>
    /// <param name="options">Supplies the grant TTL.</param>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public ContentGrantTable(PasskeyOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _table = new BearerTokenTable<GrantEntry>(timeProvider);
    }

    /// <summary>Mints a new grant token valid for the configured number of minutes.</summary>
    /// <returns>The plaintext bearer token for the dashboard to retain in memory.</returns>
    public string IssueGrant()
    {
        var ttl = TimeSpan.FromMinutes(_options.GetEffectiveContentGrantMinutes());
        var expires = _table.TimeProvider.GetUtcNow().Add(ttl);
        return _table.Issue(_ => new GrantEntry(ExpiresAtUtc: expires));
    }

    /// <summary>Returns whether <paramref name="token"/> is a known, unexpired grant.</summary>
    public bool IsValid(string? token) =>
        _table.TryPeek(token, getExpiry: e => e.ExpiresAtUtc);

    /// <summary>Revokes a single grant token.</summary>
    public bool Revoke(string? token) => _table.Revoke(token);

    /// <summary>Revokes every outstanding grant (for example after "Lock").</summary>
    public void RevokeAll() => _table.RevokeAll();

    private sealed record GrantEntry(DateTimeOffset ExpiresAtUtc);
}
