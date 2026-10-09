namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Discriminates which body a framed record inside a session file carries. ADR-0019 stores the
/// client's exchange always, the provider-side pair only when a translator rewrote the traffic, and a
/// metadata snapshot and the text extracts with every turn.
/// </summary>
public enum SessionBodyKind : byte
{
    /// <summary>Bytes the client sent, captured before request decoding.</summary>
    ClientRequest = 1,

    /// <summary>Bytes relayed back to the client, with no size cap.</summary>
    ClientResponse = 2,

    /// <summary>Provider-facing request after translation (only when it differs).</summary>
    ProviderRequest = 3,

    /// <summary>Provider-facing response before reverse translation (only when it differs).</summary>
    ProviderResponse = 4,

    /// <summary>
    /// A JSON snapshot of the routing facts at capture time (models, tokens, cost, status, latency, normalized
    /// harness), so an export never joins a store with its own retention.
    /// </summary>
    TurnMetadata = 5,

    /// <summary>
    /// A JSON object holding the text extracts the learning jobs and the Sessions tab read: the newest user
    /// message and the assistant reply text.
    /// </summary>
    Extracts = 6,
}
