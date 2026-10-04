using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// The effective web-interface address and CA trust anchor, written to a well-known machine-shared
/// location so a companion process (the Windows tray, an install script) can find the running router
/// without re-parsing <c>appsettings.json</c> - which, per
/// <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>'s Phase P1
/// design note, is unreliable for this purpose: the effective value can come from an environment
/// variable, the command line, or an operator overlay, and an MSI upgrade re-lays the installed
/// <c>appsettings.json</c> to its packaged defaults.
/// </summary>
/// <param name="WebUrl">
/// The web GUI's effective base URL (e.g. <c>https://localhost:47104</c>), or <see langword="null"/> if
/// the web listener never bound. Written by <c>ProxyHostedService</c> once the listener actually starts.
/// </param>
/// <param name="CaThumbprint">
/// The SHA-256 thumbprint of the local CA a client should trust to see a clean HTTPS connection, or
/// <see langword="null"/> if the CA could not be loaded.
/// </param>
public sealed record WebInterfaceDiscoveryInfo(string? WebUrl, string? CaThumbprint)
{
    /// <summary>Gets when this file was written, in UTC.</summary>
    public DateTimeOffset WrittenAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads and writes <see cref="WebInterfaceDiscoveryInfo"/> at a fixed, machine-shared path. Not a
/// secret: the URL and CA thumbprint are exactly what a legitimate client needs to find and trust the
/// router, and both are otherwise discoverable by anyone who can already reach the machine. So on Windows
/// it carries an explicit <c>BUILTIN\Users</c> read rule (ADR-0024 rule 4): the data directory around it
/// grants only <c>SYSTEM</c> and <c>Administrators</c>, and the unelevated tray reads this file by its full
/// path, which needs no traverse right on the directory.
/// </summary>
public static class WebInterfaceDiscoveryFile
{
    /// <summary>The file name within <see cref="StorageOptions.ResolveMachineSharedDirectory"/>.</summary>
    public const string FileName = "web-interface.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Resolves the file's absolute path under the machine-shared data directory.</summary>
    private static string ResolvePath()
    {
        return Path.Combine(path1: StorageOptions.ResolveMachineSharedDirectory(), path2: FileName);
    }

    /// <summary>
    /// Writes <paramref name="info"/> to <paramref name="path"/> (or the resolved default), creating the
    /// containing directory if needed.
    /// </summary>
    public static void Write(WebInterfaceDiscoveryInfo info, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        var resolvedPath = path ?? ResolvePath();
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path: resolvedPath, contents: JsonSerializer.Serialize(value: info, options: SerializerOptions));

        if (OperatingSystem.IsWindows()) GrantUsersRead(resolvedPath);
    }

    /// <summary>
    /// Adds an explicit read rule for <c>BUILTIN\Users</c> to <paramref name="path"/>, keeping whatever it
    /// inherits. The rule is added once and survives later rewrites, which keep the file's security.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void GrantUsersRead(string path)
    {
        var users = new SecurityIdentifier(sidType: WellKnownSidType.BuiltinUsersSid, null);
        var file = new FileInfo(path);
        var security = file.GetAccessControl();

        var alreadyGranted = security.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow && users.Equals(rule.IdentityReference) &&
                         rule.FileSystemRights.HasFlag(FileSystemRights.Read));
        if (alreadyGranted) return;

        security.AddAccessRule(new FileSystemAccessRule(identity: users, fileSystemRights: FileSystemRights.Read,
            type: AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    /// <summary>
    /// Reads <see cref="WebInterfaceDiscoveryInfo"/> from <paramref name="path"/> (or the resolved
    /// default), returning <see langword="null"/> if the file is missing or cannot be parsed - a
    /// companion process should treat either as "no running router found" rather than fail.
    /// </summary>
    public static WebInterfaceDiscoveryInfo? TryRead(string? path = null)
    {
        var resolvedPath = path ?? ResolvePath();

        try
        {
            if (!File.Exists(resolvedPath)) return null;
            return JsonSerializer.Deserialize<WebInterfaceDiscoveryInfo>(json: File.ReadAllText(resolvedPath),
                options: SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
