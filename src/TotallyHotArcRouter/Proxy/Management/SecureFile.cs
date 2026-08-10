using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The per-user file-protection sequence shared by every secret persisted to
/// <c>%LOCALAPPDATA%\TotallyHotArcRouter\</c>: create the file empty and closed, restrict it to the
/// current user (Windows ACL, or POSIX mode 600), and only then write the content - in that order, so
/// the secret is never briefly readable under the file's default/inherited permissions. Factored out of
/// <see cref="ManagementAccessToken"/>, whose ordering this preserves exactly, so
/// <see cref="ProtectedSecretStore"/> (and any future caller) does not reimplement it.
/// </summary>
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
