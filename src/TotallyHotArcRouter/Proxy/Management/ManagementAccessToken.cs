using System.Security.Cryptography;
using System.Text;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Generates, persists, and verifies the single per-machine bearer/shared-secret token that gates every
/// management surface (gRPC admin services and the MCP endpoint alike).
/// </summary>
/// <remarks>
/// <para>
/// Persisted in <see cref="ProtectedSecretStore"/> under <see cref="SecretName"/> (web GUI migration plan
/// Phase P9) - the same DPAPI-on-Windows/Data-Protection-elsewhere-protected store every provider
/// credential already lives in, rather than the plaintext, ACL-restricted-but-still-plaintext
/// <c>management-token.txt</c> file this replaces. <see cref="GetOrCreate"/> imports that legacy file
/// once, on the first call after upgrading past this phase: an install that already handed its token to
/// MCP clients must not silently mint a fresh one and invalidate every one of them. The legacy file is
/// deleted only after a successful import, so a failed import (the store's key ring not yet writable, say)
/// leaves the old file in place to retry from next time rather than losing the token outright.
/// </para>
/// <para>
/// <see cref="ProtectedSecretStore"/> already serializes its own read-modify-write cycle with a path-scoped
/// named <see cref="Mutex"/> (see its remarks), so this type no longer needs one of its own the way its
/// pre-P9, plain-file-based predecessor did.
/// </para>
/// </remarks>
public static class ManagementAccessToken
{
    /// <summary>The secret name this token is stored under in <see cref="ProtectedSecretStore"/>.</summary>
    internal const string SecretName = "management:token";

    /// <summary>The legacy plaintext file name <see cref="GetOrCreate"/> imports from, once, if found.</summary>
    private const string LegacyTokenFileName = "management-token.txt";

    /// <summary>
    /// Loads the persisted token if one already exists in <paramref name="store"/>, importing it from the
    /// legacy plaintext file if that is where it still lives, otherwise generates a new cryptographically
    /// random one, persists it, and returns it.
    /// </summary>
    /// <param name="store">The secret store to read/write; defaults to the machine-shared default store.</param>
    /// <param name="legacyTokenPath">
    /// The legacy plaintext token file to import from if present; only meant for tests (which must not
    /// touch this machine's real <c>management-token.txt</c>). Production callers omit it, getting the
    /// real default machine-shared path.
    /// </param>
    public static string GetOrCreate(ProtectedSecretStore? store = null, string? legacyTokenPath = null)
    {
        var secretStore = store ?? new ProtectedSecretStore();
        var resolvedLegacyPath = legacyTokenPath ?? DefaultLegacyPath();

        // GetOrAdd - not a separate TryRead-then-Write - holds one mutex across the whole check, import,
        // and (if neither found anything) generate-and-persist sequence, so two callers racing to be
        // "the first" (two router instances starting together, say) can never both conclude "nothing
        // stored yet" and each write a competing token.
        return secretStore.GetOrAdd(name: SecretName,
            valueFactory: () => TryImportLegacyToken(legacyTokenPath: resolvedLegacyPath) ?? GenerateToken());
    }

    /// <summary>
    /// Unconditionally generates a fresh token, persists it in <paramref name="store"/>, and returns it -
    /// overwriting whatever was there. Backs <see cref="IManagementTokenProvider.Regenerate"/> (web GUI
    /// migration plan Phase P4): unlike <see cref="GetOrCreate"/>, which only ever creates a token the
    /// first time, this always mints a new one, so every caller holding the old value stops authenticating.
    /// </summary>
    /// <param name="store">The secret store to write to; defaults to the machine-shared default store.</param>
    public static string Regenerate(ProtectedSecretStore? store = null)
    {
        var secretStore = store ?? new ProtectedSecretStore();

        var token = GenerateToken();
        secretStore.Write(name: SecretName, value: token);
        return token;
    }

    /// <summary>
    /// Compares <paramref name="presented"/> against <paramref name="expected"/> in constant time (so a
    /// caller probing the endpoint cannot learn anything about the correct token from response timing).
    /// A length mismatch is safe to short-circuit on: it leaks only "wrong length", not which bytes
    /// differ.
    /// </summary>
    public static bool Verify(string? presented, string expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        if (string.IsNullOrEmpty(presented)) return false;

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return presentedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(left: presentedBytes, right: expectedBytes);
    }

    /// <summary>
    /// Reads the token from <paramref name="legacyTokenPath"/> if that file still exists and is
    /// non-empty, deleting it afterward so a later call never re-imports it. Returns
    /// <see langword="null"/> (leaving the legacy file untouched) when there is nothing to import or the
    /// read/delete itself fails, so <see cref="GetOrCreate"/>'s factory falls through to minting a fresh
    /// token rather than losing an operator's already-distributed one to a transient failure. Only ever
    /// called from inside <see cref="ProtectedSecretStore.GetOrAdd"/>'s held mutex, so there is no race
    /// with another caller also trying to import the same file.
    /// </summary>
    private static string? TryImportLegacyToken(string legacyTokenPath)
    {
        try
        {
            if (!File.Exists(legacyTokenPath)) return null;

            var imported = File.ReadAllText(legacyTokenPath).Trim();
            if (string.IsNullOrEmpty(imported)) return null;

            File.Delete(legacyTokenPath);
            return imported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the default legacy plaintext token file path
    /// (<c>%ProgramData%\TotallyHotArcRouter\management-token.txt</c> on Windows; see
    /// <see cref="AppDataPaths"/> for every other platform) - the file a pre-P9 install wrote via the now-
    /// retired file-based <c>ManagementAccessToken</c>.
    /// </summary>
    private static string DefaultLegacyPath()
    {
        return Path.Combine(path1: AppDataPaths.ResolveMachineSharedDirectory(), path2: LegacyTokenFileName);
    }

    /// <summary>Generates a fresh 32-byte cryptographically random token, base64url-encoded (no padding).</summary>
    private static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
