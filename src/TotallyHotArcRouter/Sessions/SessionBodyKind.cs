namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Discriminates which body a framed record inside a session file carries. ADR-0019 stores the
/// client's exchange always, and the provider-side pair only when a translator rewrote the traffic.
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
}
