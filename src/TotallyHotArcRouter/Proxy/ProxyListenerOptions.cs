namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Configuration for <see cref="ProxyServer"/>'s Kestrel listeners, bound from the <c>Proxy</c> section.
/// Replaces the plain <c>port</c>/<c>grpcPort</c> constructor integers
/// <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>'s Phase P1
/// retires, so bind address and the opt-in plain-HTTP listener have somewhere to live alongside the
/// port itself.
/// </summary>
public sealed class ProxyListenerOptions
{
    /// <summary>Gets the configuration section name used for proxy listener settings.</summary>
    public const string SectionName = "Proxy";

    /// <summary>
    /// Gets the port Kestrel listens on for plain HTTP/1.1 LLM-forwarding traffic and <c>/v1/models</c>.
    /// Defaults to <c>47101</c> - inside IANA's dynamic/private range, chosen to avoid the well-known
    /// collisions the router's original 5000s-range ports had (iperf's default of 5001, Synology DSM's
    /// default HTTPS admin port, macOS AirPlay Receiver on the adjacent 5000). <c>0</c> binds an
    /// ephemeral port (test-only; see <see cref="ProxyServer"/>'s remarks).
    /// </summary>
    public int Port { get; init; } = 47101;

    /// <summary>
    /// Gets the address <see cref="Port"/> binds to: <c>"loopback"</c> (the default - dual-stack
    /// 127.0.0.1/::1, matching today's behavior), <c>"any"</c>/<c>"0.0.0.0"</c>/<c>"::"</c> (dual-stack
    /// all-interfaces, for the Docker case where "loopback" means the container's own network
    /// namespace), or a literal IP address.
    /// </summary>
    public string BindAddress { get; init; } = "loopback";

    /// <summary>
    /// Gets the opt-in plain-HTTP listener for LLM clients that cannot trust a custom CA (D6a in the web
    /// GUI migration plan). Off by default; always loopback-only when enabled - see
    /// <see cref="PlainHttpListenerOptions"/>'s remarks for why it has no bind-address setting of its own.
    /// </summary>
    public PlainHttpListenerOptions PlainHttp { get; init; } = new();
}

/// <summary>
/// Configuration for the opt-in plain-HTTP LLM-proxy listener (D6a in
/// <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>): an escape
/// hatch for tools that ignore the OS trust store and cannot be pointed at the router's local CA. Serves
/// LLM proxy routes only - never gRPC, the GUI, auth, or MCP - and is deliberately not
/// <see cref="ProxyListenerOptions.BindAddress"/>-configurable: unlike the primary proxy port, this
/// listener carries unencrypted traffic, so it must never be reachable from outside the machine even
/// when an operator has set the primary port to bind non-loopback for a container deployment.
/// </summary>
public sealed class PlainHttpListenerOptions
{
    /// <summary>
    /// Gets whether the listener starts at all. Defaults to <see langword="false"/>. The router logs a
    /// Warning at startup when this is <see langword="true"/>, since it means unencrypted LLM traffic is
    /// being served (loopback-only, but still plaintext on the wire to that first hop).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the loopback-only port the listener binds when <see cref="Enabled"/>. Defaults to
    /// <c>47105</c>, the next port after MCP's default (47103) and the web port (47104) - also chosen to
    /// avoid the Java Debug Wire Protocol's near-universal default debug port of 5005, the original
    /// scheme's equivalent value.
    /// </summary>
    public int Port { get; init; } = 47105;
}
