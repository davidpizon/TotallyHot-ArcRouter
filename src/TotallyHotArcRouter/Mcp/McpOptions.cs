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
}

