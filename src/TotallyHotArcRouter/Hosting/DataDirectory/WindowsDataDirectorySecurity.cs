using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// The access rule set a protected Windows data directory carries (ADR-0024 rule 1): which accounts get
/// full control, inherited by every file and subfolder, and which account owns the directory when the
/// router creates it.
/// </summary>
/// <remarks>
/// <see cref="Machine"/> is the only policy production code uses. The type exists so tests can run the
/// real ACL code unelevated: an unelevated process cannot make <c>Administrators</c> the owner of anything,
/// and cannot open a directory granted only to <c>SYSTEM</c> and <c>Administrators</c>, so tests add the
/// current account to both lists instead.
/// </remarks>
/// <param name="Owner">The owner assigned to a directory the router creates.</param>
/// <param name="FullControl">
/// The accounts granted full control with <c>(OI)(CI)</c> inheritance. These are also the only accounts
/// accepted as an existing directory's owner: an owner can always rewrite its own DACL, so trusting any
/// other owner would make the DACL check meaningless.
/// </param>
[SupportedOSPlatform("windows")]
public sealed record WindowsDirectoryPolicy(SecurityIdentifier Owner, IReadOnlyList<SecurityIdentifier> FullControl)
{
    /// <summary>The <c>NT AUTHORITY\SYSTEM</c> SID, the account the installed service runs as.</summary>
    public static SecurityIdentifier LocalSystem { get; } = new(sidType: WellKnownSidType.LocalSystemSid, null);

    /// <summary>The <c>BUILTIN\Administrators</c> SID.</summary>
    public static SecurityIdentifier Administrators { get; } =
        new(sidType: WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// The production policy: owned by <c>Administrators</c>, full control for <c>SYSTEM</c> and
    /// <c>Administrators</c> only, and no ACE for any individual account (plan decision 4).
    /// </summary>
    public static WindowsDirectoryPolicy Machine { get; } = new(Owner: Administrators,
        FullControl: [LocalSystem, Administrators]);

    /// <summary>Whether <paramref name="sid"/> may own a protected directory under this policy.</summary>
    public bool IsTrustedOwner(SecurityIdentifier sid)
    {
        return FullControl.Contains(sid);
    }
}

/// <summary>
/// Creates and verifies the protected DACL on a Windows data directory (ADR-0024 rules 1 and 2). One
/// protected, inheritable DACL on the root covers every file beneath it, including the <c>-wal</c> and
/// <c>-shm</c> files SQLite deletes and re-creates, which a per-file ACL cannot (plan finding F5).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDataDirectorySecurity
{
    /// <summary>
    /// Builds the security descriptor a protected directory is created with: <paramref name="policy"/>'s
    /// owner, inheritance from the parent broken, and full control with <c>(OI)(CI)</c> for each of
    /// <paramref name="policy"/>'s accounts and nobody else.
    /// </summary>
    public static DirectorySecurity BuildSecurity(WindowsDirectoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var security = new DirectorySecurity();
        security.SetOwner(policy.Owner);
        // Protected, and the inherited rules discarded rather than copied: %ProgramData%'s own ACL grants
        // BUILTIN\Users read and create rights (finding F1), which is exactly what this replaces.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in policy.FullControl)
            security.AddAccessRule(new FileSystemAccessRule(identity: sid,
                fileSystemRights: FileSystemRights.FullControl,
                inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));

        return security;
    }

    /// <summary>
    /// Creates <paramref name="path"/> with <paramref name="policy"/>'s DACL applied atomically, in the
    /// same <c>CreateDirectory</c> call, so the directory never exists under its parent's ACL - not even
    /// for the instant a create-then-restrict sequence would leave open.
    /// </summary>
    /// <exception cref="IOException">Something already exists at <paramref name="path"/>.</exception>
    public static void CreateProtected(string path, WindowsDirectoryPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.Exists(path))
            throw new IOException($"Cannot create a protected data directory at '{path}': something already exists there.");

        new DirectoryInfo(path).Create(BuildSecurity(policy));
    }

    /// <summary>
    /// Inspects <paramref name="path"/> without following it if it is a link. A directory is
    /// <see cref="DataDirectoryState.Protected"/> only if all of these hold:
    /// <list type="bullet">
    /// <item><description>it is a real directory, not a junction, symlink or mount point;</description></item>
    /// <item><description>its owner is trusted by <paramref name="policy"/>;</description></item>
    /// <item><description>its DACL is protected, so it inherits nothing from its parent;</description></item>
    /// <item><description>no allow rule names an account outside <paramref name="policy"/>;</description></item>
    /// <item><description>
    /// every account in <paramref name="policy"/> holds full control inherited by every file and folder, and
    /// none of them is denied anything.
    /// </description></item>
    /// </list>
    /// </summary>
    public static DataDirectoryInspection Inspect(string path, WindowsDirectoryPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(policy);

        FileAttributes attributes;
        try
        {
            // GetAttributes does not follow a reparse point, so a junction is reported as one here instead
            // of as the directory it points at.
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DataDirectoryInspection.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return DataDirectoryInspection.Inaccessible;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            return new DataDirectoryInspection(DataDirectoryState.Unprotected,
                Reason: "it is a junction, symbolic link or mount point");

        if (!attributes.HasFlag(FileAttributes.Directory))
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, Reason: "it is a file, not a directory");

        DirectorySecurity security;
        try
        {
            security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner |
                                                                AccessControlSections.Access);
        }
        catch (UnauthorizedAccessException)
        {
            return DataDirectoryInspection.Inaccessible;
        }

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, Reason: "its owner could not be read");

        var ownerDisplay = Describe(owner);
        if (!policy.IsTrustedOwner(owner))
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, Owner: ownerDisplay,
                Reason: $"it is owned by {ownerDisplay}, which can rewrite its permissions");

        if (!security.AreAccessRulesProtected)
            return new DataDirectoryInspection(DataDirectoryState.Unprotected, Owner: ownerDisplay, OwnerTrusted: true,
                Reason: "it inherits permissions from its parent directory");

        const InheritanceFlags inheritToEverything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        HashSet<SecurityIdentifier> fullyGranted = [];
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            var sid = rule.IdentityReference as SecurityIdentifier;
            var isPolicyAccount = sid is not null && policy.FullControl.Contains(sid);

            if (rule.AccessControlType == AccessControlType.Deny)
            {
                // A deny on another account narrows nothing that matters; one on SYSTEM or Administrators would
                // lock the service or the recovering administrator out of the data.
                if (isPolicyAccount)
                    return new DataDirectoryInspection(DataDirectoryState.Unprotected, Owner: ownerDisplay,
                        OwnerTrusted: true, Reason: $"it denies access to {Describe(rule.IdentityReference)}");
                continue;
            }

            if (!isPolicyAccount)
                return new DataDirectoryInspection(DataDirectoryState.Unprotected, Owner: ownerDisplay,
                    OwnerTrusted: true, Reason: $"it grants access to {Describe(rule.IdentityReference)}");

            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
                (rule.InheritanceFlags & inheritToEverything) == inheritToEverything &&
                !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
                !rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit))
                fullyGranted.Add(sid!);
        }

        // Every policy account must hold full control inherited by every file and folder: a protected DACL
        // granting SYSTEM only create rights would pass the write probe yet leave the databases unreadable.
        foreach (var required in policy.FullControl)
            if (!fullyGranted.Contains(required))
                return new DataDirectoryInspection(DataDirectoryState.Unprotected, Owner: ownerDisplay,
                    OwnerTrusted: true,
                    Reason: $"it does not grant {Describe(required)} inherited full control");

        return new DataDirectoryInspection(DataDirectoryState.Protected, Owner: ownerDisplay, OwnerTrusted: true);
    }

    /// <summary>
    /// Reads the owner of <paramref name="path"/> without following a link, or <see langword="null"/> when
    /// it cannot be read.
    /// </summary>
    public static SecurityIdentifier? TryGetOwner(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemSecurity security = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Formats an account for a log line: its name when Windows can translate it, otherwise its SID.</summary>
    public static string Describe(IdentityReference identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return identity.Value;
        }
    }

    /// <summary>
    /// Whether this process holds administrative rights right now: it runs as <c>SYSTEM</c>, or its token
    /// has the <c>Administrators</c> group enabled. An administrator's unelevated (UAC-filtered) token
    /// does not count - that is the account ADR-0024 keeps out.
    /// </summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
