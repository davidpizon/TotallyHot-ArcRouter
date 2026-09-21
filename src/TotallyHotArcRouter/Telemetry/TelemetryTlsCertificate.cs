using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Generates, persists, and reloads a self-signed <c>CN=localhost</c> TLS certificate. Introduced after
/// unencrypted HTTP/2 (h2c) turned out to be unreliable in practice: on at least one managed/corporate
/// Windows machine, every <c>StreamEvents</c> connection attempt failed with the HTTP/2-level
/// <c>HTTP_1_1_REQUIRED</c> error, consistent with something on the network path (VPN client,
/// endpoint security agent, TLS-inspecting proxy - common on managed machines, even for loopback
/// traffic) not understanding or silently mangling the h2c connection preface. Real TLS + ALPN
/// negotiation is far less likely to be interfered with, since it looks like any other HTTPS
/// connection until decrypted. See docs/router/grpc-migration.md's "Transport" section.
/// </summary>
/// <remarks>
/// <para>
/// <b>No longer wired into any production Kestrel listener as of the web GUI migration plan's Phase P7.</b>
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> and <c>McpServer</c> both issue their TLS
/// certificates from <see cref="LocalCertificateAuthority.GetOrCreateLeaf()"/> instead - a router-generated,
/// name-constrained local CA whose leaf every listener shares, trusted once per machine via
/// <c>--install-certificate</c> (ADR-0013), rather than each listener/client pair negotiating its own
/// self-signed-and-hand-trusted certificate the way this class's <c>CN=localhost</c> callback-based trust
/// model required. This class is kept only for its own still-passing unit tests
/// (<c>TelemetryTlsCertificateTests</c>) and as the historical record of the h2c-unreliability finding
/// above, which is still true and still the reason every listener uses TLS at all - nothing currently
/// calls <see cref="GetOrCreate"/> in production, and the parameterless overload that resolved the
/// machine-shared paths itself was deleted once the scan confirmed it had no callers at all. The
/// caller-supplied paths are now the only way in. The client-side trust callback this remarks section
/// used to describe (<c>TotallyHot.ArcRouter.Gui.Services.LiveDataStore</c>) belonged to the retired MAUI
/// GUI; its closest surviving analog is <c>TotallyHot.ArcRouter.Gui.Telemetry.TelemetryChannelFactory
/// .ValidateLoopbackCertificate</c>, kept for the Tray's own native-gRPC channel (see that type's remarks).
/// </para>
/// <para>
/// Persisted under the machine-shared data directory (<see cref="AppDataPaths"/>; web GUI migration plan
/// Phase P3 moved this off the per-user <c>%LOCALAPPDATA%\TotallyHotArcRouter\</c> location
/// docs/router/signalr-hub-security.md originally proposed) as a password-protected <c>.pfx</c>, with the
/// random runtime password stored in <see cref="ProtectedSecretStore"/> - so the certificate survives
/// process restarts instead of being regenerated (and thus needing the client to re-trust a new one)
/// every launch. The password itself: on Windows it stays sealed with user-scoped DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>), tied to the encrypting account regardless of the
/// file's directory - if a different account ever starts the router, password resolution fails cleanly and
/// a fresh certificate/password pair is generated (see <see cref="TryResolvePassword"/>), rather than
/// silently succeeding with the wrong owner.
/// </para>
/// </remarks>
public static class TelemetryTlsCertificate
{
    /// <summary>
    /// The protected secret store's name for the certificate password (<c>docs/router/secrets-at-rest-plan.md</c>
    /// §3's naming convention).
    /// </summary>
    private const string PasswordSecretName = "telemetry:cert-password";

    /// <summary>
    /// Loads the persisted certificate if one already exists, otherwise generates a new self-signed one
    /// (subject <c>CN=localhost</c>, with <c>localhost</c>/loopback IPs as Subject Alternative Names, valid
    /// two years) and persists it before returning it. Takes explicit paths and a secret store rather than
    /// resolving them itself, so a test can point it at a temporary directory.
    /// </summary>
    /// <param name="certificatePath">The <c>.pfx</c> file path.</param>
    /// <param name="passwordPath">
    /// The legacy plaintext password file path (<c>docs/router/secrets-at-rest-plan.md</c> §6): read and
    /// migrated into <paramref name="secretStore"/> when the store holds no entry yet, then deleted.
    /// </param>
    /// <param name="secretStore">The protected secret store the password is read from and persisted to.</param>
    internal static X509Certificate2 GetOrCreate(string certificatePath, string passwordPath,
        ProtectedSecretStore secretStore)
    {
        var directory = Path.GetDirectoryName(certificatePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(certificatePath) && TryResolvePassword(passwordPath: passwordPath, secretStore: secretStore,
                password: out var existingPassword))
            return X509CertificateLoader.LoadPkcs12FromFile(path: certificatePath, password: existingPassword,
                keyStorageFlags: X509KeyStorageFlags.Exportable);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName: "CN=localhost", key: rsa,
            hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(enhancedKeyUsages: [new Oid("1.3.6.1.5.5.7.3.1")],
                false)); // Server Authentication

        using var certificate = request.CreateSelfSigned(
            notBefore: DateTimeOffset.UtcNow
                .AddDays(-1), // Slight backdate to avoid immediate not-yet-valid clock-skew issues.
            notAfter: DateTimeOffset.UtcNow.AddYears(2));

        // Random per-installation password, not a hardcoded literal - there is nothing checked into
        // source or compiled into either binary that would let a stray reader of the source decrypt
        // an intercepted .pfx.
        var password = Guid.NewGuid().ToString("N");
        var certificateBytes = certificate.Export(contentType: X509ContentType.Pkcs12, password: password);

        File.WriteAllBytes(path: certificatePath, bytes: certificateBytes);
        StorePassword(secretStore: secretStore, password: password);

        return X509CertificateLoader.LoadPkcs12(data: certificateBytes, password: password,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Resolves the certificate's password: the protected store first, falling back to (and migrating in)
    /// a legacy plaintext password file from before <see cref="ProtectedSecretStore"/> supported every
    /// platform (web GUI migration plan Phase P3). Returns <see langword="false"/> when neither holds a
    /// password - a certificate file with no recoverable password must be regenerated, not opened with a
    /// guessed one.
    /// </summary>
    private static bool TryResolvePassword(string passwordPath, ProtectedSecretStore secretStore, out string password)
    {
        if (secretStore.TryRead(name: PasswordSecretName, value: out password)) return true;

        if (!File.Exists(passwordPath))
        {
            password = string.Empty;
            return false;
        }

        password = File.ReadAllText(passwordPath);

        // One-time migration of a pre-Phase-P3 plaintext file into the now-always-available protected
        // store. Deliberately no longer catches PlatformNotSupportedException here - the whole point of
        // Phase P3's pluggable protector is that ProtectedSecretStore.Write works on every platform, so a
        // failure here is a real problem (a locked-down key directory, a full disk) that should surface,
        // not be silently downgraded back to "leave the plaintext file in place".
        secretStore.Write(name: PasswordSecretName, value: password);
        File.Delete(passwordPath);

        return true;
    }

    /// <summary>Persists a freshly generated password to the protected store.</summary>
    private static void StorePassword(ProtectedSecretStore secretStore, string password)
    {
        secretStore.Write(name: PasswordSecretName, value: password);
    }
}