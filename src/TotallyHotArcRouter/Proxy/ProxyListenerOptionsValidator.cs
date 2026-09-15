using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Mcp;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Validates <see cref="ProxyListenerOptions"/> against itself and, for the opt-in plain-HTTP listener
/// (D6a in <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>),
/// against every other configured port - a silent collision would otherwise surface at runtime as one of
/// two listeners simply failing to bind, with no indication of which configuration value caused it.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProxyListenerOptionsValidator"/> class.
/// </remarks>
/// <param name="webInterfaceOptions">The web GUI listener's options, read lazily so registration order never matters.</param>
/// <param name="mcpOptions">The MCP endpoint's options, read lazily so registration order never matters.</param>
public sealed class ProxyListenerOptionsValidator(
    IOptionsMonitor<WebInterfaceOptions> webInterfaceOptions,
    IOptionsMonitor<McpOptions> mcpOptions) : IValidateOptions<ProxyListenerOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ProxyListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePortRange(port: options.Port, name: nameof(ProxyListenerOptions.Port), failures: failures);

        if (options.PlainHttp.Enabled)
        {
            var plainHttpPort = options.PlainHttp.Port;

            // 0 (ephemeral) is meaningful for the primary port in tests, but never for this opt-in
            // listener: an operator points an already-configured tool at a stable port, so a port that
            // moves on every restart defeats the feature entirely.
            if (plainHttpPort is <= 0 or > 65535)
                failures.Add(
                    $"{nameof(ProxyListenerOptions.PlainHttp)}.{nameof(PlainHttpListenerOptions.Port)} must be between 1 and 65535 when enabled, but was {plainHttpPort}.");

            if (plainHttpPort == options.Port)
                failures.Add(
                    $"{nameof(ProxyListenerOptions.PlainHttp)}.{nameof(PlainHttpListenerOptions.Port)} ({plainHttpPort}) collides with {nameof(ProxyListenerOptions.Port)}.");

            if (plainHttpPort == webInterfaceOptions.CurrentValue.Port)
                failures.Add(
                    $"{nameof(ProxyListenerOptions.PlainHttp)}.{nameof(PlainHttpListenerOptions.Port)} ({plainHttpPort}) collides with {nameof(WebInterfaceOptions)}.{nameof(WebInterfaceOptions.Port)}.");

            if (plainHttpPort == mcpOptions.CurrentValue.Port)
                failures.Add(
                    $"{nameof(ProxyListenerOptions.PlainHttp)}.{nameof(PlainHttpListenerOptions.Port)} ({plainHttpPort}) collides with {nameof(McpOptions)}.{nameof(McpOptions.Port)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePortRange(int port, string name, List<string> failures)
    {
        // 0 (ephemeral) is valid here - see ProxyServer's remarks on port: 0's test-only use.
        if (port is < 0 or > 65535)
            failures.Add($"{name} must be between 0 and 65535, but was {port}.");
    }
}
