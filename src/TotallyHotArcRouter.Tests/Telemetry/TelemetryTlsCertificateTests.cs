using System.Runtime.InteropServices;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryTlsCertificate"/>'s password storage: a freshly generated password lands in
/// the protected secret store rather than the legacy plaintext file, an already-persisted certificate's
/// password round-trips through the store, and a legacy plaintext password file is migrated in and
/// removed (<c>docs/router/secrets-at-rest-plan.md</c> §6).
/// </summary>
public sealed class TelemetryTlsCertificateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));

    private string CertificatePath => Path.Combine(_directory, "telemetry-cert.pfx");
    private string PasswordPath => Path.Combine(_directory, "telemetry-cert-pwd.txt");
    private string SecretStorePath => Path.Combine(_directory, "secrets.dat");

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void GetOrCreate_NoExistingCertificate_GeneratesOne_AndStoresThePasswordInTheProtectedStore()
    {
        if (!IsWindows)
        {
            return;
        }

        var secretStore = new ProtectedSecretStore(SecretStorePath);

        using var certificate = TelemetryTlsCertificate.GetOrCreate(CertificatePath, PasswordPath, secretStore);

        Assert.True(File.Exists(CertificatePath));
        Assert.False(File.Exists(PasswordPath));
        Assert.True(secretStore.TryRead("telemetry:cert-password", out var password));
        Assert.False(string.IsNullOrWhiteSpace(password));
        Assert.Equal("localhost", certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false));
    }

    [Fact]
    public void GetOrCreate_CalledTwice_ReturnsTheSamePersistedCertificate()
    {
        if (!IsWindows)
        {
            return;
        }

        var secretStore = new ProtectedSecretStore(SecretStorePath);

        using var first = TelemetryTlsCertificate.GetOrCreate(CertificatePath, PasswordPath, secretStore);
        using var second = TelemetryTlsCertificate.GetOrCreate(CertificatePath, PasswordPath, secretStore);

        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void GetOrCreate_LegacyPasswordFile_MigratesItIntoTheStore_AndDeletesTheFile()
    {
        if (!IsWindows)
        {
            return;
        }

        // Seed a certificate + password via a throwaway store, then reconstruct the pre-Phase-3 world: the
        // password moved onto a plaintext file and removed from the store.
        var seedStore = new ProtectedSecretStore(Path.Combine(_directory, "seed-secrets.dat"));
        using (TelemetryTlsCertificate.GetOrCreate(CertificatePath, PasswordPath, seedStore))
        {
        }

        Assert.True(seedStore.TryRead("telemetry:cert-password", out var seededPassword));
        seedStore.Delete("telemetry:cert-password");
        File.WriteAllText(PasswordPath, seededPassword);

        var freshStore = new ProtectedSecretStore(SecretStorePath);
        using var certificate = TelemetryTlsCertificate.GetOrCreate(CertificatePath, PasswordPath, freshStore);

        Assert.False(File.Exists(PasswordPath));
        Assert.True(freshStore.TryRead("telemetry:cert-password", out var migratedPassword));
        Assert.Equal(seededPassword, migratedPassword);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
