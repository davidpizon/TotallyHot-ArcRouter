using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Hosting.DataDirectory;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Publishes the local CA's public certificate (<c>router-ca.crt</c>) to a directory every local account
/// can read but only the service and administrators can write (ADR-0024 rule 4). Clients import it by
/// path - Firefox, <c>NODE_EXTRA_CA_CERTS</c>, <c>SSL_CERT_FILE</c>, <c>curl --cacert</c>; see
/// <c>docs/router/client-tls-setup.md</c> - and the data directory that used to hold it is now readable
/// by administrators only.
/// </summary>
/// <remarks>
/// <para>The directory, per platform:</para>
/// <list type="bullet">
/// <item><description>Windows: <c>%ProgramData%\TotallyHotArcRouter-Public</c>.</description></item>
/// <item><description>Linux: systemd's <c>RUNTIME_DIRECTORY</c> (<c>/run/totallyhot-arcrouter</c>, mode <c>0755</c>).</description></item>
/// <item><description>macOS: <c>/Library/Application Support/TotallyHotArcRouter-Public</c>, created by <c>install.sh</c>.</description></item>
/// <item><description>The container image: <c>/public</c>.</description></item>
/// </list>
/// <para>
/// It holds a trust anchor users import, so a copy planted by another account would make them trust the
/// attacker's root. The directory therefore gets the same owner and link checks as the data directory, and
/// one that fails them is not written to.
/// </para>
/// </remarks>
public static class PublicCaCertificate
{
    /// <summary>The published certificate's file name.</summary>
    private const string FileName = "router-ca.crt";

    /// <summary>The Windows and macOS directory name, beside the data directory.</summary>
    private const string DirectoryName = AppDataPaths.ApplicationDirectoryName + "-Public";

    private const UnixFileMode PublicDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                     UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                                     UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                                     UnixFileMode.OtherExecute;

    private const UnixFileMode PublicFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Gets this platform's public certificate directory.</summary>
    public static string ResolveDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                DirectoryName);

        if (OperatingSystem.IsMacOS()) return Path.Combine("/Library/Application Support", DirectoryName);

        var runtimeDirectory = Environment.GetEnvironmentVariable("RUNTIME_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory)) return runtimeDirectory.Split(':', 2)[0];

        return Environment.GetEnvironmentVariable(DataDirectoryBootstrap.ContainerEnvironmentVariable) == "1"
            ? "/public"
            : "/run/totallyhot-arcrouter";
    }

    /// <summary>
    /// Writes <paramref name="caCertificate"/>'s public PEM to <see cref="ResolveDirectory"/>, creating the
    /// directory with its public, write-protected permissions if it is missing. Returns the file's path, or
    /// <see langword="null"/> with a reason when the directory is missing and cannot be created, or exists
    /// but fails the squat checks.
    /// </summary>
    public static (string? Path, string? Problem) Publish(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        var directory = ResolveDirectory();
        var problem = OperatingSystem.IsWindows() ? PrepareWindows(directory) : PrepareUnix(directory);
        if (problem is not null) return (null, problem);

        var path = Path.Combine(directory, FileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, caCertificate.ExportCertificatePem());

        // Set explicitly rather than left to the umask: the service runs with UMask=0077, which would make
        // a newly written certificate 0600 and unreadable to the users who need to import it.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, PublicFileMode);

        File.Move(temporaryPath, path, overwrite: true);
        return (path, null);
    }

    [SupportedOSPlatform("windows")]
    private static string? PrepareWindows(string directory)
    {
        var policy = WindowsDirectoryPolicy.Machine;
        if (!Path.Exists(directory))
        {
            if (!WindowsDataDirectorySecurity.IsElevated()) return "it does not exist, and creating it needs elevation";

            var security = WindowsDataDirectorySecurity.BuildSecurity(policy);
            security.AddAccessRule(new FileSystemAccessRule(
                identity: new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                fileSystemRights: FileSystemRights.ReadAndExecute,
                inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
            new DirectoryInfo(directory).Create(security);
            return null;
        }

        var attributes = File.GetAttributes(directory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
            return "it is a link or a file, so it was not created by this router";

        var existing = new DirectoryInfo(directory).GetAccessControl();
        if (existing.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !policy.IsTrustedOwner(owner))
            return "it is not owned by SYSTEM or Administrators, so it was not created by this router";

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var usersCanRead = false;
        foreach (FileSystemAccessRule rule in existing.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && users.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                usersCanRead = true;

            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && policy.FullControl.Contains(sid)) continue;

            const FileSystemRights writeRights = FileSystemRights.Write | FileSystemRights.Delete |
                                                 FileSystemRights.ChangePermissions |
                                                 FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;
            if (users.Equals(rule.IdentityReference) && (rule.FileSystemRights & writeRights) == 0) continue;

            return $"it grants {WindowsDataDirectorySecurity.Describe(rule.IdentityReference)} more than read access";
        }

        // Publishing into a directory unelevated clients cannot read would succeed and help nobody.
        return usersCanRead ? null : "it does not grant BUILTIN\\Users read access, so clients could not read the certificate";
    }

    [UnsupportedOSPlatform("windows")]
    private static string? PrepareUnix(string directory)
    {
        var status = UnixNative.LStat(directory);
        if (status is null)
        {
            try
            {
                Directory.CreateDirectory(directory, PublicDirectoryMode);
                // mkdir's mode is masked by the umask (0077 under the service), so set it explicitly.
                File.SetUnixFileMode(directory, PublicDirectoryMode);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "it does not exist and could not be created";
            }
        }

        if (status.Value.Kind != UnixFileKind.Directory) return "it is a link or a file, so it was not created for this router";

        // Root acting for the service (sudo ... --export-ca) also trusts the service account, which owns the
        // directory systemd or install.sh created.
        if (status.Value.Uid != 0 && !DataDirectoryBootstrap.TrustedUnixOwners().Contains(status.Value.Uid))
            return "it is owned by another account, so it was not created for this router";

        // Group or other write bits would let another account replace the certificate.
        if ((status.Value.Mode & 0x12) != 0) return "it is writable by other accounts";

        // And without other read and search bits, clients could not reach the certificate at all.
        if ((status.Value.Mode & 0x5) != 0x5) return "it is not readable by other accounts";

        return null;
    }
}
