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
/// Persisted under <c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c>, the same per-user directory
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
    /// Gets the default token file path (<c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c>), the
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
    /// Restricts <paramref name="path"/> to the current user and writes <paramref name="token"/> to it,
    /// via the shared <see cref="SecureFile.WriteRestricted(string, byte[])"/> sequence.
    /// </summary>
    private static void WriteRestricted(string path, string token) =>
        SecureFile.WriteRestricted(path, Encoding.UTF8.GetBytes(token));
}

