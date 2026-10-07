namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Stores single-use bearer authorizations bound to a gated operation and its parameters (ADR-0020).
/// Consumption removes the entry before binding is checked so concurrent replay gets at most one success.
/// </summary>
public sealed class OneOperationAuthorizationTable
{
    /// <summary>How long an issued authorization remains valid.</summary>
    public static readonly TimeSpan AuthorizationTtl = TimeSpan.FromMinutes(2);

    private readonly BearerTokenTable<AuthorizationEntry> _table;

    /// <summary>Initializes a new instance of the <see cref="OneOperationAuthorizationTable"/> class.</summary>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public OneOperationAuthorizationTable(TimeProvider? timeProvider = null)
    {
        _table = new BearerTokenTable<AuthorizationEntry>(timeProvider);
    }

    /// <summary>Issues a one-operation token bound to <paramref name="operation"/> and <paramref name="parameters"/>.</summary>
    public string Issue(string operation, string parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        parameters ??= string.Empty;
        var binding = GatedOperation.FormatBinding(operation, parameters);
        var expires = _table.TimeProvider.GetUtcNow().Add(AuthorizationTtl);
        return _table.Issue(_ => new AuthorizationEntry(Binding: binding, ExpiresAtUtc: expires));
    }

    /// <summary>
    /// Consumes <paramref name="token"/> and returns whether it matched the expected binding. The entry is
    /// always removed when found, even if the binding or expiry check fails afterward.
    /// </summary>
    public bool TryConsume(string? token, string operation, string parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        parameters ??= string.Empty;
        var expected = GatedOperation.FormatBinding(operation, parameters);
        if (!_table.TryConsume(token, out var entry, getExpiry: e => e.ExpiresAtUtc) || entry is null)
            return false;

        return entry.Binding == expected;
    }

    private sealed record AuthorizationEntry(string Binding, DateTimeOffset ExpiresAtUtc);
}
