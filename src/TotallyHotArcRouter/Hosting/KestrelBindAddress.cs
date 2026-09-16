using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Resolves a <see cref="Proxy.ProxyListenerOptions.BindAddress"/>/<see cref="Proxy.WebInterfaceOptions.BindAddress"/>
/// string into a Kestrel listen call, shared by every inner host that grew a configurable bind address
/// for <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see> (loopback
/// by default, "any" for the Docker case where "loopback" means the container's own network namespace).
/// </summary>
internal static class KestrelBindAddress
{
    /// <summary>
    /// Binds <paramref name="port"/> on <paramref name="options"/> per <paramref name="bindAddress"/>,
    /// preserving <see cref="Proxy.ProxyServer"/>'s pre-existing ephemeral-port special case: a fixed port
    /// binds dual-stack (both IPv4 and IPv6) via <c>KestrelServerOptions.ListenLocalhost</c>/
    /// <c>KestrelServerOptions.ListenAnyIP</c> when the mode calls for it, but port <c>0</c> must bind a
    /// single address - those two methods would otherwise assign the IPv4 and IPv6 listeners two
    /// different ephemeral ports.
    /// </summary>
    /// <param name="options">The Kestrel options being configured.</param>
    /// <param name="bindAddress">
    /// <c>"loopback"</c> (dual-stack 127.0.0.1/::1), <c>"any"</c>/<c>"0.0.0.0"</c>/<c>"::"</c>
    /// (dual-stack all-interfaces), or a literal IP address string.
    /// </param>
    /// <param name="port">The port to bind. <c>0</c> binds an ephemeral port on a single address.</param>
    /// <param name="configure">Per-listener configuration (protocols, TLS), or <see langword="null"/> for Kestrel's defaults.</param>
    /// <exception cref="FormatException"><paramref name="bindAddress"/> is not a recognized mode and not a valid IP address.</exception>
    public static void Listen(KestrelServerOptions options, string bindAddress, int port,
        Action<ListenOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);

        var mode = Normalize(bindAddress);

        if (port == 0)
        {
            // Dual-stack convenience methods assign the IPv4 and IPv6 listeners independent ephemeral
            // ports when port is 0 - bind a single representative address instead, matching the exact
            // special case ProxyServer's constructor already carved out for its plain-HTTP port.
            var singleAddress = mode switch
            {
                "loopback" => IPAddress.Loopback,
                "any" => IPAddress.Any,
                _ => IPAddress.Parse(bindAddress)
            };
            ListenSingle(options: options, address: singleAddress, port: port, configure: configure);
            return;
        }

        switch (mode)
        {
            case "loopback":
                ListenLocalhost(options: options, port: port, configure: configure);
                break;
            case "any":
                ListenAnyIp(options: options, port: port, configure: configure);
                break;
            default:
                ListenSingle(options: options, address: IPAddress.Parse(bindAddress), port: port, configure: configure);
                break;
        }
    }

    /// <summary>
    /// Resolves <paramref name="bindAddress"/> to one of the three recognized modes (<c>"loopback"</c>,
    /// <c>"any"</c>, or the literal address itself for later <see cref="IPAddress.Parse(string)"/>ing),
    /// case-insensitively and accepting <c>"0.0.0.0"</c>/<c>"::"</c> as spellings of <c>"any"</c>.
    /// </summary>
    private static string Normalize(string bindAddress)
    {
        var trimmed = bindAddress.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "loopback" => "loopback",
            "any" or "0.0.0.0" or "::" => "any",
            _ => trimmed
        };
    }

    private static void ListenSingle(KestrelServerOptions options, IPAddress address, int port,
        Action<ListenOptions>? configure)
    {
        if (configure is null) options.Listen(address: address, port: port);
        else options.Listen(address: address, port: port, configure: configure);
    }

    private static void ListenLocalhost(KestrelServerOptions options, int port, Action<ListenOptions>? configure)
    {
        if (configure is null) options.ListenLocalhost(port: port);
        else options.ListenLocalhost(port: port, configure: configure);
    }

    private static void ListenAnyIp(KestrelServerOptions options, int port, Action<ListenOptions>? configure)
    {
        if (configure is null) options.ListenAnyIP(port: port);
        else options.ListenAnyIP(port: port, configure: configure);
    }
}
