using Microsoft.AspNetCore.DataProtection;
using Serilog;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Hosting;

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
    /// <summary>
    /// Reads the secret named <paramref name="name"/>. Returns <see langword="false"/> when it is not stored, or on a
    /// platform that cannot decrypt the store.
    /// </summary>
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

    /// <summary>
    /// Removes the secret named <paramref name="name"/>. Returns <see langword="false"/> when nothing was stored
    /// under it.
    /// </summary>
    bool Delete(string name);

    /// <summary>
    /// Reports whether a secret is stored under <paramref name="name"/> - the value itself is never returned by this
    /// surface.
    /// </summary>
    bool Exists(string name);

    /// <summary>
    /// Removes every secret whose name starts with <paramref name="prefix"/>. Returns the number removed - the
    /// cascade cleanup for a removed provider/header.
    /// </summary>
    int DeleteByPrefix(string prefix);
}

/// <summary>
/// The generic, name-keyed protected secret store (<c>docs/router/secrets-at-rest-plan.md</c> §3): one
/// encrypted JSON map persisted to <c>secrets.dat</c> under the machine-shared data directory (see
/// <see cref="AppDataPaths"/>), backing both <see cref="ISecretReader"/> (the router's resolution path)
/// and <see cref="ISecretWriter"/> (the management surface). A single implementation satisfies both
/// interfaces so a consumer's injected dependency type alone decides whether it can read secret material
/// back.
/// </summary>
/// <remarks>
/// The whole blob is encrypted as one unit rather than each value individually: that hides the *names*
/// too - a name like <c>provider:anthropic:header:x-api-key</c> is itself informative - and the data is
/// small and rarely written, so there is no cost to reading and rewriting all of it per edit. Writes reuse
/// <see cref="SecureFile.WriteMachineShared(string, byte[])"/> - the same create-then-restrict-then-write
/// ordering <see cref="ManagementAccessToken"/> uses - and are additionally serialized by a path-scoped
/// named <see cref="Mutex"/> so two store instances (the router and GUI processes) editing at once cannot
/// interleave their read-modify-write cycles and lose an entry.
/// <para>
/// <b>Pluggable protector (web GUI migration plan Phase P3).</b> On Windows the store is
/// <see cref="ProtectedData"/> with a fixed application-specific <c>optionalEntropy</c> (so another process
/// cannot trivially <c>Unprotect</c> the file on its own), producing a raw ciphertext blob with no version
/// header. The scope is <see cref="DataProtectionScope.LocalMachine"/>, <em>not</em> the
/// <see cref="DataProtectionScope.CurrentUser"/> originally used here: Phase P3 moved this file to the
/// machine-shared directory but left its protection bound to a single user profile, so the installed
/// <c>LocalSystem</c> service could not read a store written by the interactive user, or the reverse. See
/// ADR-0015, and <see cref="UnprotectWindows"/> for the in-place migration that change carries. Off
/// Windows, DPAPI does not exist, so <see cref="Write"/> used to throw
/// <see cref="PlatformNotSupportedException"/> and every read returned "not found" - callers fell through
/// to their environment-variable path, honestly degraded rather than silently writing plaintext. That
/// platform gap is what this phase closes: off Windows, the store now uses ASP.NET Core's Data Protection
/// stack with a file-system key ring under <c>&lt;data-dir&gt;/keys</c> (mode <c>0700</c>, refused rather
/// than used if an existing key directory is more permissive - see <see cref="EnsureKeyDirectorySecure"/>),
/// and every value written this way carries a one-byte format version ahead of the ciphertext (a format
/// with nothing to be backward-compatible with, since <see cref="Write"/> could never previously succeed
/// there). "Protected" now means DPAPI-strength, user-account-bound secrecy on Windows, and
/// file-permission-bound secrecy off Windows - a real difference in guarantee, stated plainly rather than
/// implied to be equivalent; see <c>docs/router/secrets-at-rest.md</c>.
/// </para>
/// </remarks>
public sealed class ProtectedSecretStore : ISecretReader, ISecretWriter
{
    private const string FileName = "secrets.dat";
    private const string KeyRingDirectoryName = "keys";
    private const string DataProtectionApplicationName = "TotallyHotArcRouter";
    private const string DataProtectionPurpose = "TotallyHotArcRouter.ProtectedSecretStore.v1";

    // The non-Windows on-disk format's one-byte version prefix, ahead of the Data-Protection-encrypted
    // payload. Bumping this is how a future format change would stay distinguishable from this one -
    // there is only ever one version in play today.
    private const byte UnixFormatVersion = 1;

    // Fixed, application-specific entropy folded into every DPAPI call. Not a secret in itself - it is
    // compiled into the binary - but it stops another process running as the same Windows user from
    // calling ProtectedData.Unprotect on this file without also carrying this exact byte sequence.
    private static readonly byte[] Entropy = "TotallyHotArcRouter.ProtectedSecretStore.v1"u8.ToArray();

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtectedSecretStore"/> class over the default
    /// machine-shared store file (see <see cref="DefaultPath"/> - moved off the per-user location by the
    /// web GUI migration plan's Phase P3).
    /// </summary>
    public ProtectedSecretStore() : this(DefaultPath())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtectedSecretStore"/> class over an explicit file path (for
    /// tests).
    /// </summary>
    /// <param name="path">The store file path.</param>
    public ProtectedSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc/>
    public bool TryRead(string name, out string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMap();
        if (map.TryGetValue(key: name, value: out var stored))
        {
            value = stored;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <inheritdoc/>
    public void Write(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMap();
        map[name] = value;
        SaveMap(map);
    }

    /// <summary>
    /// Atomically reads the secret named <paramref name="name"/>, or - if none is stored - computes one
    /// via <paramref name="valueFactory"/>, persists it, and returns it. The whole check-then-write
    /// sequence runs under one held mutex, unlike a caller composing <see cref="TryRead"/> and
    /// <see cref="Write"/> itself (which would leave a race window between the two calls: two callers
    /// both observing "not stored yet" and each writing a competing value). Use this whenever "create the
    /// first time, reuse afterward" is the actual requirement - <see cref="ManagementAccessToken.GetOrCreate"/>
    /// is exactly that case.
    /// </summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="valueFactory">Computes the value to store, invoked at most once, only when nothing is stored yet.</param>
    public string GetOrAdd(string name, Func<string> valueFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(valueFactory);

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMap();
        if (map.TryGetValue(key: name, value: out var existing) && !string.IsNullOrEmpty(existing)) return existing;

        var value = valueFactory();
        map[name] = value;
        SaveMap(map);
        return value;
    }

    /// <inheritdoc/>
    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMap();
        if (!map.Remove(name)) return false;

        SaveMap(map);
        return true;
    }

    /// <inheritdoc/>
    public bool Exists(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        return LoadMap().ContainsKey(name);
    }

    /// <inheritdoc/>
    public int DeleteByPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        using var mutex = OpenMutex();
        using var guard = new MutexGuard(mutex);

        var map = LoadMap();
        var toRemove = map.Keys.Where(k => k.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal))
            .ToList();
        foreach (var key in toRemove) map.Remove(key);

        if (toRemove.Count > 0) SaveMap(map);

        return toRemove.Count;
    }

    /// <summary>
    /// Gets the default store file path (<c>secrets.dat</c> under the machine-shared data directory - see
    /// <see cref="AppDataPaths"/>), the same directory the management token and telemetry certificate use.
    /// </summary>
    public static string DefaultPath()
    {
        return Path.Combine(path1: AppDataPaths.ResolveMachineSharedDirectory(), path2: FileName);
    }

    /// <summary>Loads the current map via the platform-appropriate protector - see <see cref="LoadMapWindows"/>/<see cref="LoadMapUnix"/>.</summary>
    private Dictionary<string, string> LoadMap()
    {
        return OperatingSystem.IsWindows() ? LoadMapWindows() : LoadMapUnix();
    }

    /// <summary>Persists the map via the platform-appropriate protector - see <see cref="SaveMapWindows"/>/<see cref="SaveMapUnix"/>.</summary>
    private void SaveMap(Dictionary<string, string> map)
    {
        if (OperatingSystem.IsWindows()) SaveMapWindows(map);
        else SaveMapUnix(map);
    }

    /// <summary>
    /// Reads and decrypts the store file, returning an empty map when it does not exist yet. A missing or
    /// unreadable file is never distinguished from an empty store - both mean "nothing stored yet" to
    /// every caller.
    /// </summary>
    /// <remarks>
    /// The "unreadable means empty" half of that contract is what <see cref="QuarantineUnreadableStore"/>
    /// implements, and it is load-bearing rather than defensive tidiness: this method used to let a
    /// <see cref="CryptographicException"/> escape, which failed host startup, which for the installed
    /// service meant the process aborted and Windows Installer reported only "Service ... failed to start.
    /// Verify that you have sufficient privileges" - a message naming neither this file nor the real cause.
    /// A store this process cannot decrypt is recoverable, because every secret in it is machine-generated
    /// and re-derivable, so it must never be fatal.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private Dictionary<string, string> LoadMapWindows()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);

        var encrypted = File.ReadAllBytes(_path);

        byte[] json;
        try
        {
            json = UnprotectWindows(encrypted);
        }
        catch (CryptographicException failure)
        {
            return QuarantineUnreadableStore(failure);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Decrypts a store blob, preferring the <see cref="DataProtectionScope.LocalMachine"/> scope this build
    /// writes and falling back to the <see cref="DataProtectionScope.CurrentUser"/> scope written before
    /// ADR-0015.
    /// </summary>
    /// <remarks>
    /// That fallback is what migrates an existing install: a legacy blob still decrypts for the user who
    /// wrote it, and the next <see cref="SaveMapWindows"/> re-seals the whole map under the machine scope,
    /// so the store converts in place with nothing lost and no separate migration step to run. Windows
    /// records the scope inside the blob and largely ignores the scope argument on <c>Unprotect</c>, so the
    /// first call often succeeds on a legacy blob by itself - this tries both rather than depending on that
    /// undocumented behavior. When the caller is <em>not</em> the user who wrote a legacy blob - the
    /// installed service, running as <c>LocalSystem</c> - both attempts fail and
    /// <see cref="LoadMapWindows"/> quarantines the file instead.
    /// </remarks>
    /// <param name="encrypted">The raw on-disk ciphertext.</param>
    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] encrypted)
    {
        try
        {
            return ProtectedData.Unprotect(encryptedData: encrypted, optionalEntropy: Entropy,
                scope: DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException)
        {
            return ProtectedData.Unprotect(encryptedData: encrypted, optionalEntropy: Entropy,
                scope: DataProtectionScope.CurrentUser);
        }
    }

    /// <summary>
    /// Renames a store file this process cannot decrypt out of the way and reports an empty store, so the
    /// caller regenerates what it needs instead of failing startup.
    /// </summary>
    /// <remarks>
    /// The file is preserved under a timestamped <c>.unreadable-*</c> name rather than deleted: this account
    /// cannot read it, but the account that wrote it may still be able to, and silently destroying the only
    /// copy to reclaim a few hundred bytes is never the right trade. Moving it aside rather than leaving it
    /// in place is also what keeps the next write from overwriting it, and what stops this from re-logging on
    /// every subsequent read. If the move itself fails the store is still reported empty - a host that cannot
    /// start is strictly worse than one that regenerates its secrets - but that case logs at error, because
    /// the next write will then overwrite a file nobody has recovered.
    /// </remarks>
    /// <param name="failure">The decryption failure, logged as the reason the store was set aside.</param>
    private Dictionary<string, string> QuarantineUnreadableStore(CryptographicException failure)
    {
        var quarantinePath = $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";

        try
        {
            File.Move(sourceFileName: _path, destFileName: quarantinePath, overwrite: true);
            Log.Warning(exception: failure,
                messageTemplate:
                "Could not decrypt the protected secret store at {StorePath}; it has been moved to {QuarantinePath} and this process is starting with an empty store. Secrets held there are regenerated, which for the local CA means clients must trust the new certificate.",
                propertyValue0: _path, propertyValue1: quarantinePath);
        }
        catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
        {
            Log.Error(exception: moveFailure,
                messageTemplate:
                "Could not decrypt the protected secret store at {StorePath}, and could not move it aside; continuing with an empty store, so the next write overwrites the existing file.",
                propertyValue: _path);
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Encrypts and persists <paramref name="map"/> via <see cref="SecureFile.WriteMachineShared"/>, so the
    /// on-disk file is DPAPI-encrypted and ACL-restricted to <c>LocalSystem</c>, the local administrators
    /// group, and the writing account.
    /// </summary>
    /// <remarks>
    /// Unlike the per-user scheme this replaced, the ACL here is doing the real work rather than backing up
    /// the encryption. <see cref="DataProtectionScope.LocalMachine"/> means any account that can read the
    /// bytes can also decrypt them - <see cref="Entropy"/> is compiled into the binary and so is not a
    /// secret - which is precisely why <see cref="SecureFile.WriteMachineShared"/> grants no
    /// <c>BUILTIN\Users</c> access. The machine scope is not optional: this store lives in the
    /// machine-shared data directory and is read both by the service running as <c>LocalSystem</c> and by an
    /// administrator running the same exe directly, and no one user profile's key can serve both.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private void SaveMapWindows(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.SerializeToUtf8Bytes(value: map, options: SerializerOptions);
        var encrypted = ProtectedData.Protect(userData: json, optionalEntropy: Entropy,
            scope: DataProtectionScope.LocalMachine);

        WriteAtomically(encrypted);
    }

    /// <summary>
    /// Reads and decrypts the store file using the Data Protection key ring, returning an empty map when
    /// it does not exist yet - see <see cref="LoadMapWindows"/>'s remarks for the same "missing = empty"
    /// contract. The one-byte format-version prefix (<see cref="UnixFormatVersion"/>) is stripped before
    /// decryption; an unrecognized version throws rather than guessing at a format this build doesn't know.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private Dictionary<string, string> LoadMapUnix()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);

        var stored = File.ReadAllBytes(_path);
        if (stored.Length < 1) return new Dictionary<string, string>(StringComparer.Ordinal);

        var version = stored[0];
        if (version != UnixFormatVersion)
            throw new InvalidOperationException(
                $"'{_path}' has secret-store format version {version}, which this build does not recognize (expected {UnixFormatVersion}).");

        var protector = CreateUnixProtector();
        var json = protector.Unprotect(stored[1..]);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Encrypts and persists <paramref name="map"/> via the Data Protection key ring, prefixed with the
    /// one-byte format version <see cref="LoadMapUnix"/> checks on read.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private void SaveMapUnix(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.SerializeToUtf8Bytes(value: map, options: SerializerOptions);
        var protector = CreateUnixProtector();
        var protectedPayload = protector.Protect(json);

        var versioned = new byte[1 + protectedPayload.Length];
        versioned[0] = UnixFormatVersion;
        protectedPayload.CopyTo(array: versioned, index: 1);

        WriteAtomically(versioned);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a temp file in the store's directory via
    /// <see cref="SecureFile.WriteMachineShared"/>, then atomically renames it over <see cref="_path"/>. A
    /// crash mid-write leaves the temp file orphaned rather than truncating <c>secrets.dat</c>, which
    /// would otherwise read back as "every secret is gone".
    /// </summary>
    private void WriteAtomically(byte[] content)
    {
        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        SecureFile.WriteMachineShared(path: tempPath, content: content);
        File.Move(sourceFileName: tempPath, destFileName: _path, true);
    }

    /// <summary>
    /// Builds the Data Protection protector this store's non-Windows format uses: a file-system key ring
    /// under <c>&lt;data-dir&gt;/keys</c> (see <see cref="EnsureKeyDirectorySecure"/>), with a fixed
    /// application name so the purpose string below is the only thing distinguishing this store's keys
    /// from any other Data Protection consumer that might someday share the same directory.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static IDataProtector CreateUnixProtector()
    {
        var keyDirectory = Path.Combine(AppDataPaths.ResolveMachineSharedDirectory(), KeyRingDirectoryName);
        EnsureKeyDirectorySecure(keyDirectory);

        var provider = DataProtectionProvider.Create(
            keyDirectory: new DirectoryInfo(keyDirectory),
            setupAction: builder => builder.SetApplicationName(DataProtectionApplicationName));
        return provider.CreateProtector(DataProtectionPurpose);
    }

    /// <summary>
    /// Creates <paramref name="keyDirectory"/> at mode <c>0700</c> if it doesn't exist yet. If it already
    /// exists with broader permissions, refuses rather than silently trusting (or silently tightening) a
    /// directory whose laxity might mean something else already depends on the wider access, or that the
    /// environment is misconfigured in a way worth surfacing rather than papering over.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="keyDirectory"/> already exists with permissions broader than the owner alone.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    private static void EnsureKeyDirectorySecure(string keyDirectory)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        if (!Directory.Exists(keyDirectory))
        {
            Directory.CreateDirectory(keyDirectory);
            File.SetUnixFileMode(path: keyDirectory, mode: ownerOnly);
            return;
        }

        var mode = File.GetUnixFileMode(keyDirectory);
        if ((mode & ~ownerOnly) != 0)
            throw new UnauthorizedAccessException(
                $"Refusing to use the secret-protection key directory '{keyDirectory}': its permissions ({mode}) grant access beyond the owner. Fix its mode to 0700, or delete it to have it recreated, before retrying.");
    }

    /// <summary>
    /// Creates the path-scoped named mutex serializing every read-modify-write cycle against this store's file across
    /// processes.
    /// </summary>
    private Mutex OpenMutex()
    {
        return new Mutex(false, name: "TotallyHot.ArcRouter.ProtectedSecretStore." +
                                      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path)))[
                                          ..32]);
    }

    /// <summary>
    /// Waits on and releases a <see cref="Mutex"/> across a scope, tolerating <see cref="AbandonedMutexException"/>
    /// the same way <see cref="ManagementAccessToken.GetOrCreate"/> does.
    /// </summary>
    private readonly struct MutexGuard : IDisposable
    {
        private readonly Mutex _mutex;

        /// <summary>Waits on <paramref name="mutex"/>, tolerating an abandoned-mutex signal from a crashed prior owner.</summary>
        /// <param name="mutex">The mutex to acquire for the scope's lifetime.</param>
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

        /// <summary>Releases the mutex acquired by the constructor.</summary>
        public void Dispose()
        {
            _mutex.ReleaseMutex();
        }
    }
}