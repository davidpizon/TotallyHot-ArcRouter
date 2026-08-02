using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Generates, persists, and verifies the single per-user bearer/shared-secret token that gates every
/// management surface (the REST <c>/admin/*</c> API and the MCP endpoint alike). Mirrors
/// <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate"/>'s persist-or-create shape: the token is
/// generated once and reused across restarts, so a caller that stored it doesn't need to re-trust a new
/// one on every launch.
/// </summary>
/// <remarks>
/// Persisted under <c>%LOCALAPPDATA%\TotallyHot.ArcRouter\management-token.txt</c>, the same per-user directory
/// the telemetry certificate uses. Unlike the certificate, this file is access-restricted on write - a
/// bearer token is the whole credential (there is no separate password protecting it the way the
/// certificate's <c>.pfx</c> has), so an ACL that keeps other local accounts from reading it is the only
/// thing standing between "loopback-only" and "any account on the machine". On Windows this sets an
/// explicit, non-inherited ACL granting only the current user; on POSIX it sets file mode 600. Both
/// processes trusting this token (the router and the GUI) run as the same OS user on the same machine,
/// which is why per-user-account access is an adequate boundary here rather than a hardened one.
/// </remarks>
public static class ManagementAccessToken
{
    private const string TokenFileName = "management-token.txt";

    /// <summary>
    /// Loads the persisted token if one already exists at <paramref name="path"/> (or the default
    /// location), otherwise generates a new cryptographically random one, persists it with a restricted
    /// ACL/file mode, and returns it.
    /// </summary>
    /// <param name="path">
    /// The token file path, or <see langword="null"/> for the default
    /// <c>%LOCALAPPDATA%\TotallyHot.ArcRouter\management-token.txt</c>.
    /// </param>
    public static string GetOrCreate(string? path = null)
    {
        var tokenPath = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;

        var directory = Path.GetDirectoryName(tokenPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A named, per-path mutex serializes the whole check-read-generate-write sequence across
        // processes - e.g. two router instances (or the router and something else calling GetOrCreate)
        // starting at the same moment. Without it, one process could observe the other's file mid-write
        // (empty or partial), conclude no valid token exists, and generate a competing one - split-brain
        // auth between whichever surfaces ended up trusting each token.
        using var mutex = new Mutex(initiallyOwned: false, MutexName(tokenPath));
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // A prior owner crashed while holding the mutex without releasing it - .NET still grants
            // ownership to this caller when this is thrown, so it's safe to proceed rather than fail
            // router startup. Whatever state the abandoned owner left the token file in (absent,
            // complete, or - rarely - mid-write) is exactly what the read/generate logic below already
            // handles.
        }

        try
        {
            if (File.Exists(tokenPath))
            {
                var existing = File.ReadAllText(tokenPath).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return existing;
                }
            }

            var token = GenerateToken();
            WriteRestricted(tokenPath, token);
            return token;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Derives a stable, path-scoped mutex name so concurrent callers targeting different token paths (e.g. in tests) don't contend on each other.</summary>
    private static string MutexName(string tokenPath) =>
        "TotallyHot.ArcRouter.ManagementAccessToken." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenPath)))[..32];

    /// <summary>
    /// Gets the default token file path (<c>%LOCALAPPDATA%\TotallyHot.ArcRouter\management-token.txt</c>), the
    /// same per-user directory <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate"/> uses.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotallyHotArcRouter", TokenFileName);

    /// <summary>
    /// Compares <paramref name="presented"/> against <paramref name="expected"/> in constant time (so a
    /// caller probing the endpoint cannot learn anything about the correct token from response timing).
    /// A length mismatch is safe to short-circuit on: it leaks only "wrong length", not which bytes
    /// differ.
    /// </summary>
    public static bool Verify(string? presented, string expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return presentedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    /// <summary>Generates a fresh 32-byte cryptographically random token, base64url-encoded (no padding).</summary>
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>
    /// Creates <paramref name="path"/>, restricts it to the current user (Windows ACL, or POSIX mode
    /// 600), and only then writes <paramref name="token"/> - in that order, so the secret is never
    /// briefly readable under the file's default/inherited permissions. The write happens through a
    /// handle opened with <see cref="FileShare.None"/> and held open until the token is fully written, so
    /// any other process trying to open the file in the meantime (e.g. a reader that isn't serialized
    /// behind <see cref="GetOrCreate"/>'s mutex, like the GUI's separate token reader) hits a sharing
    /// violation and fails fast instead of silently observing an empty file.
    /// </summary>
    private static void WriteRestricted(string path, string token)
    {
        // Create empty and closed first: applying the ACL (SetAccessControl) needs to open its own handle,
        // which would conflict with an already-open FileShare.None handle on the same path. No secret
        // content exists yet at this point, so there's nothing sensitive to expose - only after the file
        // is restricted do we reopen it exclusively to write the token.
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
        var bytes = Encoding.UTF8.GetBytes(token);
        stream.Write(bytes, 0, bytes.Length);
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

