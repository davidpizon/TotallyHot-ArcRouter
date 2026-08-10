using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Resolution surface for the protected secret store - the router's own request-path/discovery/telemetry
/// code is the only intended reader of secret material (see
/// <see cref="TotallyHot.ArcRouter.Proxy.ProviderCredentialResolver"/>, <c>BuildCostReconcilers</c>, and
/// <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate"/>). Public rather than internal only
/// because it appears as an optional constructor parameter on public types
/// (<see cref="ManagementFacade"/>, <see cref="TotallyHot.ArcRouter.Proxy.ModelRouteResolver"/>,
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>); §4's write-only invariant is upheld by
/// discipline in those types - none of them ever call <see cref="TryRead"/> while building a value
/// returned to a caller - not by this interface's accessibility. Deliberately has no way to enumerate
/// stored names - a caller must already know the name it wants.
/// </summary>
public interface ISecretReader
{
    /// <summary>Reads the secret named <paramref name="name"/>. Returns <see langword="false"/> when it is not stored, or on a platform that cannot decrypt the store.</summary>
    bool TryRead(string name, out string value);
}

/// <summary>
/// Management surface for the protected secret store - what <see cref="ManagementFacade"/> is handed.
/// Deliberately has no reader, so §4's write-only invariant
/// (<c>docs/router/secrets-at-rest-plan.md</c> §4) is a compile-time boundary rather than a convention a
/// later change can drift past.
/// </summary>
public interface ISecretWriter
{
    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/>, replacing any existing entry.</summary>
    void Write(string name, string value);

    /// <summary>Removes the secret named <paramref name="name"/>. Returns <see langword="false"/> when nothing was stored under it.</summary>
    bool Delete(string name);

    /// <summary>Reports whether a secret is stored under <paramref name="name"/> - the value itself is never returned by this surface.</summary>
    bool Exists(string name);

    /// <summary>Removes every secret whose name starts with <paramref name="prefix"/>. Returns the number removed - the cascade cleanup for a removed provider/header.</summary>
    int DeleteByPrefix(string prefix);
}

/// <summary>
/// The generic, name-keyed protected secret store (<c>docs/router/secrets-at-rest-plan.md</c> §3): one
/// DPAPI-encrypted JSON map persisted to <c>%LOCALAPPDATA%\TotallyHotArcRouter\secrets.dat</c>, backing
/// both <see cref="ISecretReader"/> (the router's resolution path) and <see cref="ISecretWriter"/> (the
/// management surface). A single implementation satisfies both interfaces so a consumer's injected
/// dependency type alone decides whether it can read secret material back.
/// </summary>
/// <remarks>
/// The whole blob is encrypted as one unit rather than each value individually: that hides the *names*
/// too - a name like <c>provider:anthropic:header:x-api-key</c> is itself informative - and the data is
/// small and rarely written, so there is no cost to reading and rewriting all of it per edit. A fixed
/// application-specific <c>optionalEntropy</c> is passed to <see cref="ProtectedData"/> so that another
/// process running as the same user cannot trivially <c>Unprotect</c> the file on its own. Writes reuse
/// <see cref="SecureFile.WriteRestricted(string, byte[])"/> - the same create-then-restrict-then-write
/// ordering <see cref="ManagementAccessToken"/> uses - and are additionally serialized by a path-scoped
/// named <see cref="Mutex"/> so two store instances (the router and GUI processes) editing at once cannot
/// interleave their read-modify-write cycles and lose an entry.
/// <para>
/// <b>Non-Windows behavior: refuse, do not degrade.</b> DPAPI is Windows-only, so <see cref="Write"/>
/// throws <see cref="PlatformNotSupportedException"/> and <see cref="TryRead"/>/<see cref="Exists"/>
/// return <see langword="false"/> on every other platform - callers fall through to their existing
/// environment-variable path. A store named "protected" that silently wrote plaintext instead would be
/// worse than today's honest plaintext, so this never happens.
/// </para>
/// </remarks>
public sealed class ProtectedSecretStore : ISecretReader, ISecretWriter
{
    private const string FileName = "secrets.dat";

    // Fixed, application-specific entropy folded into every DPAPI call. Not a secret in itself - it is
    // compiled into the binary - but it stops another process running as the same Windows user from
    // calling ProtectedData.Unprotect on this file without also carrying this exact byte sequence.
    private static readonly byte[] Entropy = "TotallyHotArcRouter.ProtectedSecretStore.v1"u8.ToArray();

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="ProtectedSecretStore"/> class over the default per-user store file.</summary>
    public ProtectedSecretStore() : this(DefaultPath())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProtectedSecretStore"/> class over an explicit file path (for tests).</summary>
    /// <param name="path">The store file path.</param>
    public ProtectedSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Gets the default store file path (<c>%LOCALAPPDATA%\TotallyHotArcRouter\secrets.dat</c>), the same per-user directory the management token and telemetry certificate use.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotallyHotArcRouter", FileName);

    /// <inheritdoc />
    public bool TryRead(string name, out string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!OperatingSystem.IsWindows())
        {
            value = string.Empty;
            return false;
        }

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMapWindows();
        if (map.TryGetValue(name, out var stored))
        {
            value = stored;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Write(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The protected secret store requires Windows DPAPI and is unavailable on this platform.");
        }

        WriteWindows(name, value);
    }

    /// <inheritdoc />
    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMapWindows();
        if (!map.Remove(name))
        {
            return false;
        }

        SaveMapWindows(map);
        return true;
    }

    /// <inheritdoc />
    public bool Exists(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        return LoadMapWindows().ContainsKey(name);
    }

    /// <inheritdoc />
    public int DeleteByPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMapWindows();
        var toRemove = map.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in toRemove)
        {
            map.Remove(key);
        }

        if (toRemove.Count > 0)
        {
            SaveMapWindows(map);
        }

        return toRemove.Count;
    }

    [SupportedOSPlatform("windows")]
    private void WriteWindows(string name, string value)
    {
        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMapWindows();
        map[name] = value;
        SaveMapWindows(map);
    }

    /// <summary>
    /// Reads and decrypts the store file, returning an empty map when it does not exist yet. A missing or
    /// unreadable file is never distinguished from an empty store - both mean "nothing stored yet" to
    /// every caller.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private Dictionary<string, string> LoadMapWindows()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var encrypted = File.ReadAllBytes(_path);
        var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Encrypts and persists <paramref name="map"/> via <see cref="SecureFile.WriteRestricted"/>, so the
    /// on-disk file is both DPAPI-encrypted and ACL-restricted to the current user - belt and suspenders,
    /// since DPAPI's <see cref="DataProtectionScope.CurrentUser"/> scope already ties decryption to the
    /// user's profile, but the ACL also keeps another local account from even reading the ciphertext.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void SaveMapWindows(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(map, SerializerOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

        // Atomic overwrite: write to a temp file in the same directory, then File.Move over the real
        // path. A crash mid-write leaves the temp file orphaned rather than truncating secrets.dat, which
        // would otherwise read back as "every secret is gone".
        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        SecureFile.WriteRestricted(tempPath, encrypted);
        File.Move(tempPath, _path, overwrite: true);
    }

    private Mutex OpenMutex() =>
        new(initiallyOwned: false, "TotallyHot.ArcRouter.ProtectedSecretStore." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path)))[..32]);

    /// <summary>Waits on and releases a <see cref="Mutex"/> across a scope, tolerating <see cref="AbandonedMutexException"/> the same way <see cref="ManagementAccessToken.GetOrCreate"/> does.</summary>
    private readonly struct MutexGuard : IDisposable
    {
        private readonly Mutex _mutex;

        public MutexGuard(Mutex mutex)
        {
            _mutex = mutex;
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // A prior owner crashed while holding the mutex without releasing it - .NET still grants
                // ownership to this caller when this is thrown, so it's safe to proceed: whatever state
                // the abandoned owner left the store file in (absent, complete, or mid-write) is exactly
                // what the load/save logic already handles.
            }
        }

        public void Dispose() => _mutex.ReleaseMutex();
    }
}
