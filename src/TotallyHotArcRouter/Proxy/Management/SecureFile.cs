using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The file-protection sequence shared by every secret this application persists: create the file empty
/// and closed, restrict its ACL, and only then write the content - in that order, so the secret is never
/// briefly readable under the file's default/inherited permissions. Factored out of
/// <see cref="ManagementAccessToken"/>, whose ordering this preserves exactly, so
/// <see cref="ProtectedSecretStore"/> (and any future caller) does not reimplement it.
/// </summary>
/// <remarks>
/// Two audiences, so two entry points. <see cref="WriteRestricted"/> is the per-user form used for secrets
/// only one OS account ever touches (Windows ACL granting the current user, or POSIX mode 600).
/// <see cref="WriteMachineShared"/> is the cross-account form for a credential the <c>LocalSystem</c>
/// service and the interactive-user GUI must both read; see its remarks for why that case exists and what
/// it costs.
/// </remarks>
internal static class SecureFile
{
    /// <summary>
    /// Creates <paramref name="path"/>, restricts it to the current user, and only then writes
    /// <paramref name="content"/> through a handle opened with <see cref="FileShare.None"/> held open
    /// until the content is fully written - so another process trying to open the file in the meantime
    /// hits a sharing violation and fails fast instead of silently observing a partial write.
    /// </summary>
    public static void WriteRestricted(string path, byte[] content)
    {
        // Create empty and closed first: applying the ACL (SetAccessControl) needs to open its own
        // handle, which would conflict with an already-open FileShare.None handle on the same path. No
        // secret content exists yet at this point, so there's nothing sensitive to expose - only after
        // the file is restricted do we reopen it exclusively to write the content.
        using (File.Create(path))
        {
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RestrictToCurrentUserWindows(path);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(content, 0, content.Length);
        stream.Flush();
    }

    /// <summary>
    /// Creates <paramref name="path"/>, restricts it to machine-wide accounts, and only then writes
    /// <paramref name="content"/> - the counterpart to <see cref="WriteRestricted"/> for a secret that two
    /// processes running as <em>different</em> OS accounts must both read.
    /// </summary>
    /// <remarks>
    /// The installed configuration runs the router as <c>LocalSystem</c> (see
    /// <c>TotallyHotArcRouter.Installer/Package.wxs</c>) while the GUI runs as the interactive user, so a
    /// credential written with <see cref="WriteRestricted"/>'s current-user-only ACL is unreadable by the
    /// other side - the two processes silently end up with different tokens and every management call comes
    /// back 401. This grants full control to <c>LocalSystem</c> and the local administrators group (the
    /// accounts that write it) and read-only access to <c>Users</c> (the interactive account that presents
    /// it). That deliberately widens the boundary from "one user account" to "any interactive account on
    /// this machine" - the minimum needed for the cross-account handoff to work at all, and still far
    /// narrower than the file's default inherited ACL.
    /// </remarks>
    /// <param name="path">The file to create and protect.</param>
    /// <param name="content">The secret bytes to write once the file is protected.</param>
    public static void WriteMachineShared(string path, byte[] content)
    {
        // Same create-empty-then-restrict-then-write ordering as WriteRestricted, and for the same reason:
        // the secret must never exist on disk under the file's default/inherited permissions.
        using (File.Create(path))
        {
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RestrictToMachineAccountsWindows(path);
        }
        else
        {
            // 0644: owner writes, everyone reads - the POSIX equivalent of the Windows ACL above.
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(content, 0, content.Length);
        stream.Flush();
    }

    /// <summary>
    /// Breaks ACL inheritance on <paramref name="path"/> and grants full control to the writing account,
    /// <c>LocalSystem</c>, and the local administrators group, plus read-only access to <c>Users</c>.
    /// </summary>
    /// <remarks>
    /// The current-user grant is not redundant with the <c>Users</c> one and must not be dropped: protecting
    /// the DACL discards the inherited rules that were the writer's only access, and <c>Users</c> is granted
    /// <see cref="FileSystemRights.Read"/>, so without it the very next step - reopening the file to write
    /// the secret - fails with <see cref="UnauthorizedAccessException"/> for any writer that isn't
    /// <c>LocalSystem</c> or an administrator. That is the ordinary case for a developer running the router
    /// directly rather than as the installed service.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void RestrictToMachineAccountsWindows(string path)
    {
        var security = new FileSecurity();
        // Break inheritance and drop every inherited rule first, so the rules below are the complete access
        // list - not "these plus whatever the parent folder already allowed".
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // These two are well-known SIDs present on every Windows installation, so unlike
        // RestrictToCurrentUserWindows there is no resolution failure to guard against here.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        // Read, not FullControl: the interactive user presents this credential but never mints it, so write
        // access would let any local account replace the token the service trusts.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            FileSystemRights.Read,
            AccessControlType.Allow));

        // See the remarks: without this, a writer who is neither LocalSystem nor an administrator locks
        // itself out of the file it is in the middle of creating.
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>
    /// Breaks ACL inheritance on <paramref name="path"/> and grants full control to only the current
    /// Windows user. If the current user's SID can't be resolved, the file's inherited ACL is left
    /// untouched rather than applying a protected-but-empty DACL, which would lock out every account
    /// (including the router process itself) on next read.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUserWindows(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
        {
            return;
        }

        var security = new FileSecurity();
        // Break inheritance and drop every inherited rule first, so the only access granted is the one
        // rule added below - not "current user plus whatever the parent folder already allowed".
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}
