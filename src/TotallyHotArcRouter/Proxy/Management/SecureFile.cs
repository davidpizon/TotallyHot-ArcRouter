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
/// Two audiences, so two entry points, and <b>picking the wrong one is the failure mode this class has
/// already produced once</b> - see ADR-0015. Choose by asking which OS accounts must read the file, not by
/// which name sounds stricter.
/// <list type="bullet">
/// <item>
/// <see cref="WriteMachineShared"/> - for a secret in the machine-shared data directory that the
/// <c>LocalSystem</c> service and an administrator running the router directly must both read. This is what
/// <see cref="ProtectedSecretStore"/> uses, and the only one with callers today.
/// </item>
/// <item>
/// <see cref="WriteRestricted"/> - the per-user form, granting only the writing account (Windows ACL, or
/// POSIX mode 600). <b>It currently has no callers.</b> It is retained deliberately rather than deleted as
/// dead code: <c>docs/router/security-hardening-plan.md</c> prescribes it as the remedy for four separate
/// findings that have not yet been implemented (the telemetry <c>.pfx</c> and its password fallback, the
/// operational databases, and <c>model-routing.json</c>), and that plan's own header states its per-finding
/// statuses have not been re-audited. Do not reach for it merely because a secret feels like it should be
/// private to one account - if the installed service has to read the file, it must be
/// <see cref="WriteMachineShared"/>, because the service runs as <c>LocalSystem</c> and a
/// <see cref="WriteRestricted"/> file written by anyone else is unreadable to it.
/// </item>
/// </list>
/// </remarks>
internal static class SecureFile
{
    /// <summary>
    /// Creates <paramref name="path"/>, restricts it to the current user, and only then writes
    /// <paramref name="content"/> through a handle opened with <see cref="FileShare.None"/> held open
    /// until the content is fully written - so another process trying to open the file in the meantime
    /// hits a sharing violation and fails fast instead of silently observing a partial write.
    /// </summary>
    /// <remarks>
    /// Has no callers - deliberately, for the reasons set out on the type. Suppressed rather than deleted so
    /// the retention decision does not have to be re-argued on every scan.
    /// </remarks>
    // ReSharper disable once UnusedMember.Global
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
            RestrictToCurrentUserWindows(path);
        else
            File.SetUnixFileMode(path: path, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var stream = new FileStream(path: path, mode: FileMode.Create, access: FileAccess.Write,
            share: FileShare.None);
        stream.Write(buffer: content, 0, count: content.Length);
        stream.Flush();
    }

    /// <summary>
    /// Creates <paramref name="path"/>, restricts it to the machine's administrative accounts, and only
    /// then writes <paramref name="content"/> - the counterpart to <see cref="WriteRestricted"/> for a
    /// secret that processes running as <em>different</em> OS accounts must both read.
    /// </summary>
    /// <remarks>
    /// The installed configuration runs the router as <c>LocalSystem</c> (see
    /// <c>TotallyHotArcRouter.Installer/Package.wxs</c>), but a developer or operator also runs the same exe
    /// directly as themselves. A secret in the machine-shared data directory written with
    /// <see cref="WriteRestricted"/>'s current-user-only ACL is therefore unreadable by whichever of the two
    /// did not create it, and that is not a degraded mode but a hard startup failure: the service exits with
    /// <see cref="UnauthorizedAccessException"/> and the SCM reports only "failed to start ... verify that
    /// you have sufficient privileges". That is why <see cref="ProtectedSecretStore"/> must write through
    /// this method rather than <see cref="WriteRestricted"/>.
    /// <para>
    /// Grants full control to <c>LocalSystem</c>, the local administrators group, and the writing account -
    /// and nothing else. There is deliberately <em>no</em> <c>BUILTIN\Users</c> grant: this method
    /// originally carried a read-only one so the interactive-user GUI could read the shared management
    /// token, but ADR-0012 replaced that handoff with a loopback session cookie, so the tray now
    /// authenticates with no credential of its own (see <c>TrayApplicationContext</c>) and every remaining
    /// reader of the store runs inside the <c>LocalSystem</c> router process. Holding the ACL at
    /// administrator-only matters because <see cref="ProtectedSecretStore"/> seals the store with DPAPI's
    /// <see cref="System.Security.Cryptography.DataProtectionScope.LocalMachine"/> scope and a fixed,
    /// compiled-in entropy value: that entropy is not a secret, so this ACL - not the encryption - is what
    /// keeps one local account from reading another's secrets.
    /// </para>
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
            RestrictToMachineAccountsWindows(path);
        else
            // 0600, matching the Windows ACL above now that it no longer grants BUILTIN\Users read: off
            // Windows the store is sealed by the Data Protection key ring (itself mode 0700, see
            // ProtectedSecretStore.EnsureKeyDirectorySecure) and the only reader is the router process, so
            // this file has nothing it needs to expose to other accounts. It was 0644 while the Windows side
            // still granted Users read.
            File.SetUnixFileMode(path: path, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var stream = new FileStream(path: path, mode: FileMode.Create, access: FileAccess.Write,
            share: FileShare.None);
        stream.Write(buffer: content, 0, count: content.Length);
        stream.Flush();
    }

    /// <summary>
    /// Breaks ACL inheritance on <paramref name="path"/> and grants full control to <c>LocalSystem</c>, the
    /// local administrators group, and the writing account - and to nothing else.
    /// </summary>
    /// <remarks>
    /// The current-user grant is load-bearing and must not be dropped: protecting the DACL discards the
    /// inherited rules that were the writer's only access, so without it the very next step - reopening the
    /// file to write the secret - fails with <see cref="UnauthorizedAccessException"/> for any writer that
    /// is not <c>LocalSystem</c> or an administrator, the ordinary case for a developer running the router
    /// directly rather than as the installed service. This rule was previously documented as non-redundant
    /// with a <c>BUILTIN\Users</c> read grant; that grant is gone (see <see cref="WriteMachineShared"/>'s
    /// remarks), which makes this rule the writer's only access rather than merely its write access.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void RestrictToMachineAccountsWindows(string path)
    {
        var security = new FileSecurity();
        // Break inheritance and drop every inherited rule first, so the rules below are the complete access
        // list - not "these plus whatever the parent folder already allowed".
        security.SetAccessRuleProtection(true, false);

        // These two are well-known SIDs present on every Windows installation, so unlike
        // RestrictToCurrentUserWindows there is no resolution failure to guard against here.
        security.AddAccessRule(new FileSystemAccessRule(
            identity: new SecurityIdentifier(sidType: WellKnownSidType.LocalSystemSid, null),
            fileSystemRights: FileSystemRights.FullControl,
            type: AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            identity: new SecurityIdentifier(sidType: WellKnownSidType.BuiltinAdministratorsSid, null),
            fileSystemRights: FileSystemRights.FullControl,
            type: AccessControlType.Allow));

        // See the remarks: without this, a writer who is neither LocalSystem nor an administrator locks
        // itself out of the file it is in the middle of creating. No BUILTIN\Users rule is added - ADR-0012
        // removed the one reader that needed it.
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
            security.AddAccessRule(new FileSystemAccessRule(
                identity: currentUser,
                fileSystemRights: FileSystemRights.FullControl,
                type: AccessControlType.Allow));

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
        if (currentUser is null) return;

        var security = new FileSecurity();
        // Break inheritance and drop every inherited rule first, so the only access granted is the one
        // rule added below - not "current user plus whatever the parent folder already allowed".
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity: currentUser,
            fileSystemRights: FileSystemRights.FullControl,
            type: AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}