namespace TotallyHot.ArcRouter.Mcp;

/// <summary>
/// Configuration for the MCP management endpoint, bound from the <c>Mcp</c> section.
/// </summary>
public sealed class McpOptions
{
    /// <summary>Gets the configuration section name used for MCP endpoint settings.</summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// Gets whether the MCP endpoint is started at all. Defaults to <see langword="true"/>; an operator
    /// who doesn't want a second management surface listening can turn it off.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets the loopback TLS port the MCP Streamable-HTTP endpoint listens on. Defaults to <c>47103</c> -
    /// see <see cref="Proxy.ProxyListenerOptions.Port"/>'s remarks for why the router moved off the 5000s
    /// range.
    /// </summary>
    public int Port { get; init; } = 47103;

    /// <summary>
    /// Gets the address <see cref="Port"/> binds to, using the same values as
    /// <see cref="Proxy.ProxyListenerOptions.BindAddress"/>: <c>"loopback"</c> (the default), <c>"any"</c>/
    /// <c>"0.0.0.0"</c>/<c>"::"</c>, or a literal IP address. Added by
    /// <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>'s Phase P1
    /// so a Docker deployment can expose MCP the same way it exposes the proxy and web GUI ports - MCP
    /// still carries its own bearer-token auth regardless of bind address, so widening this is a
    /// deliberate operator choice, not a new unauthenticated surface.
    /// </summary>
    public string BindAddress { get; init; } = "loopback";
}