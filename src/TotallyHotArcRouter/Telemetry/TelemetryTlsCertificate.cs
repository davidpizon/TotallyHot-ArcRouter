using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Generates, persists, and reloads a self-signed TLS certificate for the telemetry gRPC endpoint
/// (<see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>'s HTTPS/2 listener). Introduced after unencrypted
/// HTTP/2 (h2c) turned out to be unreliable in practice: on at least one managed/corporate Windows
/// machine, every <c>StreamEvents</c> connection attempt failed with the HTTP/2-level
/// <c>HTTP_1_1_REQUIRED</c> error, consistent with something on the network path (VPN client,
/// endpoint security agent, TLS-inspecting proxy - common on managed machines, even for loopback
/// traffic) not understanding or silently mangling the h2c connection preface. Real TLS + ALPN
/// negotiation is far less likely to be interfered with, since it looks like any other HTTPS
/// connection until decrypted. See docs/router/grpc-migration.md's "Transport" section.
/// </summary>
/// <remarks>
/// Persisted under <c>%LOCALAPPDATA%\TotallyHotArcRouter\</c> (the same per-user directory
/// docs/router/signalr-hub-security.md proposed for this exact purpose) as a password-protected
/// <c>.pfx</c>, with the random runtime password stored alongside it - so the certificate survives
/// process restarts instead of being regenerated (and thus needing the client to re-trust a new one)
/// every launch. The client side (<c>TotallyHot.ArcRouter.Gui.Services.LiveDataStore</c>) trusts any
/// certificate presented with subject <c>CN=localhost</c> rather than pinning this exact
/// certificate's thumbprint - both processes are the same OS user on the same machine, so this is a
/// pragmatic, adequate trust boundary for a personal local dev tool, not a hardened one. A stronger
/// follow-up would have the client read this same <c>.pfx</c> file's public certificate and pin its
/// thumbprint specifically.
/// </remarks>
public static class TelemetryTlsCertificate
{
    private const string CertificateFileName = "telemetry-cert.pfx";
    private const string PasswordFileName = "telemetry-cert-pwd.txt";

    /// <summary>The protected secret store's name for the certificate password (<c>docs/router/secrets-at-rest-plan.md</c> §3's naming convention).</summary>
    private const string PasswordSecretName = "telemetry:cert-password";

    /// <summary>
    /// Loads the persisted certificate if one already exists, otherwise generates a new self-signed
    /// one (subject <c>CN=localhost</c>, with <c>localhost</c>/loopback IPs as Subject Alternative
    /// Names, valid two years) and persists it before returning it.
    /// </summary>
    public static X509Certificate2 GetOrCreate()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotallyHotArcRouter");
        return GetOrCreate(
            Path.Combine(directory, CertificateFileName),
            Path.Combine(directory, PasswordFileName),
            new ProtectedSecretStore());
    }

    /// <summary>
    /// Overload taking explicit paths and a secret store, for tests. See <see cref="GetOrCreate()"/> for
    /// behavior.
    /// </summary>
    /// <param name="certificatePath">The <c>.pfx</c> file path.</param>
    /// <param name="passwordPath">
    /// The legacy plaintext password file path (<c>docs/router/secrets-at-rest-plan.md</c> §6): read and
    /// migrated into <paramref name="secretStore"/> when the store holds no entry yet, then deleted.
    /// </param>
    /// <param name="secretStore">The protected secret store the password is read from and persisted to.</param>
    internal static X509Certificate2 GetOrCreate(string certificatePath, string passwordPath, ProtectedSecretStore secretStore)
    {
        var directory = Path.GetDirectoryName(certificatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(certificatePath) && TryResolvePassword(passwordPath, secretStore, out var existingPassword))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(certificatePath, existingPassword, X509KeyStorageFlags.Exportable);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // Server Authentication

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), // Slight backdate to avoid immediate not-yet-valid clock-skew issues.
            DateTimeOffset.UtcNow.AddYears(2));

        // Random per-installation password, not a hardcoded literal - there is nothing checked into
        // source or compiled into either binary that would let a stray reader of the source decrypt
        // an intercepted .pfx.
        var password = Guid.NewGuid().ToString("N");
        var certificateBytes = certificate.Export(X509ContentType.Pkcs12, password);

        File.WriteAllBytes(certificatePath, certificateBytes);
        StorePassword(passwordPath, secretStore, password);

        return X509CertificateLoader.LoadPkcs12(certificateBytes, password, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Resolves the certificate's password: the protected store first, falling back to (and migrating in)
    /// the legacy plaintext file. Returns <see langword="false"/> when neither holds a password - a
    /// certificate file with no recoverable password must be regenerated, not opened with a guessed one.
    /// </summary>
    private static bool TryResolvePassword(string passwordPath, ProtectedSecretStore secretStore, out string password)
    {
        if (secretStore.TryRead(PasswordSecretName, out password))
        {
            return true;
        }

        if (!File.Exists(passwordPath))
        {
            password = string.Empty;
            return false;
        }

        password = File.ReadAllText(passwordPath);

        // Migrate the legacy file into the store, then remove it - best-effort: on a platform where the
        // store is unavailable, the legacy file stays exactly where it is and keeps working.
        try
        {
            secretStore.Write(PasswordSecretName, password);
            File.Delete(passwordPath);
        }
        catch (PlatformNotSupportedException)
        {
        }

        return true;
    }

    /// <summary>Persists a freshly generated password to the protected store, falling back to the legacy plaintext file when the store is unavailable on this platform.</summary>
    private static void StorePassword(string passwordPath, ProtectedSecretStore secretStore, string password)
    {
        try
        {
            secretStore.Write(PasswordSecretName, password);
        }
        catch (PlatformNotSupportedException)
        {
            File.WriteAllText(passwordPath, password);
        }
    }
}

