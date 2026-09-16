namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Configuration for the router-hosted web GUI's listener, bound from the <c>WebInterface</c> section.
/// Introduced by <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>'s
/// Phase P1. Live since Phase P2, which added the Kestrel binding and gRPC-Web pipeline this configures;
/// Phase P4 added the loopback-cookie/token auth <see cref="TrustLoopback"/> and
/// <see cref="AllowedHosts"/> gate on top of it. Validated at startup so a configuration mistake here is
/// caught before the listener ever binds.
/// </summary>
public sealed class WebInterfaceOptions
{
    /// <summary>Gets the configuration section name used for web-interface settings.</summary>
    public const string SectionName = "WebInterface";

    /// <summary>
    /// Gets the port the web GUI, gRPC-Web, and (from Phase P9) native gRPC listen on. Defaults to
    /// <c>47104</c> - see <see cref="ProxyListenerOptions.Port"/>'s remarks for why the router moved off
    /// the 5000s range.
    /// </summary>
    public int Port { get; init; } = 47104;

    /// <summary>
    /// Gets the address <see cref="Port"/> binds to, using the same values as
    /// <see cref="ProxyListenerOptions.BindAddress"/>: <c>"loopback"</c> (the default), <c>"any"</c>/
    /// <c>"0.0.0.0"</c>/<c>"::"</c>, or a literal IP address.
    /// </summary>
    public string BindAddress { get; init; } = "loopback";

    /// <summary>
    /// Gets the <c>Host</c> header values a request must present to be treated as loopback for the
    /// Phase P4 session-cookie fast path (ADR-0012's DNS-rebinding defense). Defaults to
    /// <c>localhost</c>, <c>127.0.0.1</c>, and <c>[::1]</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    /// Gets whether a request that looks like it came from this machine (Host/Origin/remote-IP all
    /// check out) is issued a session cookie with no credential prompt (ADR-0012). Defaults to
    /// <see langword="true"/>; an operator behind a loopback tunnel/reverse proxy (ngrok, <c>tailscale
    /// serve</c>, VS Code port forwarding, WSL2 mirrored networking) sets this to <see langword="false"/>
    /// so every session goes through token login instead.
    /// </summary>
    public bool TrustLoopback { get; init; } = true;
}
