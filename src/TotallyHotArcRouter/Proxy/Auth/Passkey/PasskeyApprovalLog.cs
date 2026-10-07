namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Bounded in-memory ring of recent passkey approvals for the dashboard audit list (ADR-0020). Fifty
/// entries cap memory while still surfacing unexpected approvals during a session.
/// </summary>
public sealed class PasskeyApprovalLog
{
    private const int MaxEntries = 50;

    private readonly Lock _lock = new();
    private readonly Queue<PasskeyApprovalEntry> _entries = new();

    /// <summary>Records one approval or refusal outcome.</summary>
    /// <param name="operation">The gated operation name.</param>
    /// <param name="credentialName">The enrolled credential display name, if known.</param>
    /// <param name="utc">When the event occurred.</param>
    /// <param name="outcome">A short outcome label such as <c>succeeded</c> or <c>failed</c>.</param>
    public void Record(string operation, string credentialName, DateTimeOffset utc, string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        credentialName ??= string.Empty;

        lock (_lock)
        {
            if (_entries.Count >= MaxEntries) _entries.Dequeue();
            _entries.Enqueue(new PasskeyApprovalEntry(operation, credentialName, utc, outcome));
        }
    }

    /// <summary>Returns the most recent entries, newest last.</summary>
    public IReadOnlyList<PasskeyApprovalEntry> Recent()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }
}

/// <summary>One row in the passkey approval audit list.</summary>
/// <param name="Operation">Gated operation name.</param>
/// <param name="CredentialName">Credential display name.</param>
/// <param name="Utc">Timestamp.</param>
/// <param name="Outcome">Outcome label.</param>
public sealed record PasskeyApprovalEntry(
    string Operation,
    string CredentialName,
    DateTimeOffset Utc,
    string Outcome);
