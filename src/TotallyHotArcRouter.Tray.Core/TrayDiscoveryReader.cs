using System.Text.Json;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// The subset of the router's discovery file the tray actually uses: where to open the dashboard.
/// Extra fields the router writes (<c>caThumbprint</c>, <c>writtenAtUtc</c>) are ignored on deserialize -
/// the tray trusts the loopback session path rather than that thumbprint, and never reads the write
/// timestamp. Kept as an independent type rather than a reference to
/// <c>TotallyHot.ArcRouter.Proxy.WebInterfaceDiscoveryInfo</c> for the same reason
/// <see cref="TrayDiscoveryReader"/> re-implements the read rather than calling
/// <c>WebInterfaceDiscoveryFile.TryRead</c>: see that type's remarks.
/// </summary>
/// <param name="WebUrl">The web dashboard's own effective address, or <see langword="null"/> if it could not be determined.</param>
public sealed record TrayDiscoveryInfo(string? WebUrl);

/// <summary>
/// Reads the router's discovery file - written by <c>ProxyHostedService.WriteDiscoveryFile</c>
/// (web GUI migration plan Phase P1/P7) - so the tray's "Show Dashboard" command and its native gRPC
/// connection can find a running router without parsing <c>appsettings.json</c> or guessing a port.
/// </summary>
/// <remarks>
/// This project deliberately doesn't reference the router executable (the same reason
/// <c>TotallyHot.ArcRouter.Gui.Admin.ManagementTokenReader</c> doesn't reference it for the management
/// token file): pulling in <c>TotallyHotArcRouter.csproj</c> - SQLite, ONNX Runtime, the full proxy
/// pipeline - to read one small JSON file would make a multi-hundred-megabyte dependency graph of a tray
/// icon. The file path and the field names it reads are duplicated here instead - the same "one fact, two
/// copies because of the process boundary" tradeoff <see cref="RoutingGateMonitor"/>'s remarks describe.
/// </remarks>
public static class TrayDiscoveryReader
{
    private const string FileName = "web-interface.json";

    /// <summary>
    /// Reads the discovery file from <paramref name="path"/> (or, when <see langword="null"/>, the default
    /// <c>%ProgramData%\TotallyHotArcRouter\web-interface.json</c>), or <see langword="null"/> when the
    /// file doesn't exist yet, is empty, or can't be parsed - the same "no running router found" semantics
    /// as <c>WebInterfaceDiscoveryFile.TryRead</c>. Never throws.
    /// </summary>
    /// <param name="path">The discovery file path override; only meant for tests. Production callers omit it.</param>
    public static TrayDiscoveryInfo? TryRead(string? path = null)
    {
        try
        {
            var filePath = path ?? DefaultPath();
            if (!File.Exists(filePath)) return null;

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<TrayDiscoveryInfo>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the default discovery file path: <c>%ProgramData%\TotallyHotArcRouter\web-interface.json</c>.
    /// Machine-wide, not per-user, matching where the router (running as <c>LocalSystem</c>) writes it -
    /// see <see cref="TrayDiscoveryReader"/>'s remarks for why this is a duplicated constant rather than a
    /// shared resolver call.
    /// </summary>
    private static string DefaultPath()
    {
        return Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            path2: "TotallyHotArcRouter",
            path3: FileName);
    }
}
