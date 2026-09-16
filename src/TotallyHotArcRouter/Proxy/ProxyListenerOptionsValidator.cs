using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Mcp;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Validates <see cref="ProxyListenerOptions"/>: its own port range, the opt-in plain-HTTP listener's
/// own range and collisions, and every pairwise port collision among the listeners that will actually
/// bind - so a silent collision surfaces as a clear startup validation failure naming both configuration
/// values, instead of one listener simply failing to bind at runtime with no indication of which setting
/// caused it. <see cref="WebInterfaceOptions"/> and <see cref="McpOptions"/>'s own port ranges are
/// validated separately by <see cref="PortRangeOptionsValidator"/> rather than here: this class takes
/// <see cref="IOptionsMonitor{TOptions}"/> dependencies on both of those types to run the collision
/// checks below, and implementing <see cref="IValidateOptions{TOptions}"/> for either of them itself
/// would be circular - building that type's <see cref="IOptionsMonitor{TOptions}"/> would require first
/// building this validator, which requires that same monitor.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProxyListenerOptionsValidator"/> class.
/// </remarks>
/// <param name="webInterfaceOptions">The web GUI listener's options, read lazily so registration order never matters.</param>
/// <param name="mcpOptions">The MCP endpoint's options, read lazily so registration order never matters.</param>
public sealed class ProxyListenerOptionsValidator(
    IOptionsMonitor<WebInterfaceOptions> webInterfaceOptions,
    IOptionsMonitor<McpOptions> mcpOptions)
    : IValidateOptions<ProxyListenerOptions>
{
    /// <summary>
    /// Validates <see cref="ProxyListenerOptions"/>: its own port range, the opt-in plain-HTTP
    /// listener's own range and collisions (only when it is actually enabled - it is the one listener
    /// here that genuinely does not bind when disabled), and - unconditionally, not gated behind the
    /// plain-HTTP listener's own <c>Enabled</c> flag the way a real bug in this class once did - the
    /// primary proxy port's collisions against every other TLS listener that is actually going to bind:
    /// <see cref="WebInterfaceOptions"/> always, <see cref="McpOptions"/> only when
    /// <see cref="McpOptions.Enabled"/>.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, ProxyListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePortRange(port: options.Port, name: nameof(ProxyListenerOptions.Port), failures: failures);

        var webPort = webInterfaceOptions.CurrentValue.Port;
        var mcpEnabled = mcpOptions.CurrentValue.Enabled;
        var mcpPort = mcpOptions.CurrentValue.Port;

        // 0 (ephemeral) is meaningful for these ports in tests - never a real collision, since each
        // ephemeral bind picks its own free port independently.
        if (options.Port != 0 && webPort != 0 && options.Port == webPort)
            failures.Add(
                $"{nameof(ProxyListenerOptions.Port)} ({options.Port}) collides with {nameof(WebInterfaceOptions)}.{nameof(WebInterfaceOptions.Port)}.");

        if (mcpEnabled && options.Port != 0 && mcpPort != 0 && options.Port == mcpPort)
            failures.Add(
                $"{nameof(ProxyListenerOptions.Port)} ({options.Port}) collides with {nameof(McpOptions)}.{nameof(McpOptions.Port)}.");

        if (mcpEnabled && webPort != 0 && mcpPort != 0 && webPort == mcpPort)
            failures.Add(
                $"{nameof(WebInterfaceOptions)}.{nameof(WebInterfaceOptions.Port)} ({webPort}) collides with {nameof(McpOptions)}.{nameof(McpOptions.Port)}.");

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

            if (plainHttpPort == webPort)
                failures.Add(
                    $"{nameof(ProxyListenerOptions.PlainHttp)}.{nameof(PlainHttpListenerOptions.Port)} ({plainHttpPort}) collides with {nameof(WebInterfaceOptions)}.{nameof(WebInterfaceOptions.Port)}.");

            // Gated on McpOptions.Enabled (a real bug fixed here): when MCP is disabled,
            // McpHostedService never binds McpOptions.Port at all, so that port is genuinely free to
            // reuse - an unconditional comparison here rejected an otherwise-valid configuration.
            if (mcpEnabled && plainHttpPort == mcpPort)
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

/// <summary>
/// Validates <see cref="WebInterfaceOptions"/> and <see cref="McpOptions"/>'s own port ranges. Kept
/// separate from <see cref="ProxyListenerOptionsValidator"/>, which owns the pairwise collision checks
/// spanning all three listener option types, because that class depends on
/// <see cref="IOptionsMonitor{TOptions}"/> of both these types - implementing
/// <see cref="IValidateOptions{TOptions}"/> for either of them there would be circular (building that
/// type's monitor would require first building the validator, which requires that same monitor). This
/// class has no dependencies, so it carries no such risk.
/// </summary>
public sealed class PortRangeOptionsValidator : IValidateOptions<WebInterfaceOptions>, IValidateOptions<McpOptions>
{
    /// <summary>Validates <see cref="WebInterfaceOptions"/>'s own port range.</summary>
    public ValidateOptionsResult Validate(string? name, WebInterfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ValidatePortRange(port: options.Port, name: nameof(WebInterfaceOptions.Port));
    }

    /// <summary>Validates <see cref="McpOptions"/>'s own port range.</summary>
    public ValidateOptionsResult Validate(string? name, McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ValidatePortRange(port: options.Port, name: nameof(McpOptions.Port));
    }

    private static ValidateOptionsResult ValidatePortRange(int port, string name)
    {
        // 0 (ephemeral) is valid here - see ProxyServer's remarks on port: 0's test-only use.
        if (port is < 0 or > 65535)
            return ValidateOptionsResult.Fail($"{name} must be between 0 and 65535, but was {port}.");

        return ValidateOptionsResult.Success;
    }
}
