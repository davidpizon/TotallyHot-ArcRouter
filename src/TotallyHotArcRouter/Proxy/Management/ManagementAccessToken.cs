using System.Security.Cryptography;
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
/// Persisted machine-wide under <c>%ProgramData%\TotallyHotArcRouter\management-token.txt</c>, alongside
/// <see cref="TotallyHot.ArcRouter.Router.RoutingGateStore"/>'s state file and for exactly the same reason:
/// the installed service runs as <c>LocalSystem</c> while the GUI runs as the interactive user, so the
/// per-user <c>%LOCALAPPDATA%</c> this used to live in resolved to a <em>different file per account</em>.
/// Each side minted or read its own token, every management call came back 401, and the GUI's tray reported
/// the (perfectly healthy) service as stopped. Note this is not the same choice as the telemetry
/// certificate's, which correctly stays per-user: only the router ever reads that <c>.pfx</c>, and its
/// password is sealed with user-scoped DPAPI, so moving it would break rather than fix it.
/// <para>
/// This file is access-restricted on write - a bearer token is the whole credential (there is no separate
/// password protecting it the way the certificate's <c>.pfx</c> has), so its ACL is the only thing standing
/// between "loopback-only" and "anything on the machine". <see cref="SecureFile.WriteMachineShared"/> grants
/// full control to <c>LocalSystem</c>/administrators and read-only access to <c>Users</c>. The boundary is
/// therefore "any interactive account on this machine" rather than "one user account" - the minimum that
/// makes the cross-account handoff work at all.
/// </para>
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
    /// <c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c>.
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
    /// Gets the default token file path (<c>%ProgramData%\TotallyHotArcRouter\management-token.txt</c>), the
    /// same machine-wide directory <see cref="TotallyHot.ArcRouter.Router.RoutingGateStore"/> persists to.
    /// Machine-wide rather than per-user because the router and the GUI do not run as the same OS account in
    /// the installed configuration - see this type's remarks.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TotallyHotArcRouter", TokenFileName);

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
    /// Restricts <paramref name="path"/> to this machine's system/administrator accounts (write) and
    /// <c>Users</c> (read), then writes <paramref name="token"/> to it, via the shared
    /// <see cref="SecureFile.WriteMachineShared(string, byte[])"/> sequence. Deliberately not the per-user
    /// <see cref="SecureFile.WriteRestricted(string, byte[])"/>: the GUI reads this file from a different OS
    /// account than the service that writes it - see this type's remarks.
    /// </summary>
    private static void WriteRestricted(string path, string token) =>
        SecureFile.WriteMachineShared(path, Encoding.UTF8.GetBytes(token));
}

