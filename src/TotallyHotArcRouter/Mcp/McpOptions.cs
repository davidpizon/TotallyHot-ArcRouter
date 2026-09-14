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
    /// Gets the loopback TLS port the MCP Streamable-HTTP endpoint listens on. Defaults to <c>5003</c>,
    /// the port immediately after the telemetry/price-source-admin gRPC port (5002).
    /// </summary>
    public int Port { get; init; } = 5003;

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