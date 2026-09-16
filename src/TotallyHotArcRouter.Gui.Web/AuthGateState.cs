namespace TotallyHot.ArcRouter.Gui.Web;

/// <summary>
/// Records the outcome of <c>Program.cs</c>'s startup <c>POST /auth/session</c> call, so
/// <see cref="AppRoot"/> can decide whether to render the dashboard directly or a token-login gate first
/// (web GUI migration plan Phase P6's own status notes named this as a known gap: a non-loopback caller -
/// Docker's default <c>WebInterface__BindAddress=0.0.0.0</c>, or an operator who set
/// <c>WebInterface:TrustLoopback=false</c> - got no session cookie and no way to obtain one from the UI,
/// just every store's ordinary "unreachable" state with nothing telling the user *why*).
/// </summary>
public sealed class AuthGateState
{
    /// <summary>
    /// Gets or sets whether the startup session call came back <c>403</c> specifically - the router is
    /// reachable and rejected the loopback fast path, so a token typed into <c>/auth/login</c> can still
    /// succeed. Left <see langword="false"/> on a network-level failure (the router isn't reachable at
    /// all yet): showing a login form in that case would be misleading, since no token would help.
    /// </summary>
    public bool LoginRequired { get; set; }
}
