using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
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
    private readonly string _directory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
        path3: Guid.NewGuid().ToString("N"));

    private string CertificatePath => Path.Combine(path1: _directory, path2: "telemetry-cert.pfx");
    private string PasswordPath => Path.Combine(path1: _directory, path2: "telemetry-cert-pwd.txt");
    private string SecretStorePath => Path.Combine(path1: _directory, path2: "secrets.dat");

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(path: _directory, true);
    }

    [Fact]
    public void GetOrCreate_NoExistingCertificate_GeneratesOne_AndStoresThePasswordInTheProtectedStore()
    {
        if (!IsWindows) return;

        var secretStore = new ProtectedSecretStore(SecretStorePath);

        using var certificate = TelemetryTlsCertificate.GetOrCreate(certificatePath: CertificatePath,
            passwordPath: PasswordPath, secretStore: secretStore);

        Assert.True(File.Exists(CertificatePath));
        Assert.False(File.Exists(PasswordPath));
        Assert.True(secretStore.TryRead(name: "telemetry:cert-password", value: out var password));
        Assert.False(string.IsNullOrWhiteSpace(password));
        Assert.Equal(expected: "localhost", actual: certificate.GetNameInfo(nameType: X509NameType.SimpleName, false));
    }

    [Fact]
    public void GetOrCreate_CalledTwice_ReturnsTheSamePersistedCertificate()
    {
        if (!IsWindows) return;

        var secretStore = new ProtectedSecretStore(SecretStorePath);

        using var first = TelemetryTlsCertificate.GetOrCreate(certificatePath: CertificatePath,
            passwordPath: PasswordPath, secretStore: secretStore);
        using var second = TelemetryTlsCertificate.GetOrCreate(certificatePath: CertificatePath,
            passwordPath: PasswordPath, secretStore: secretStore);

        Assert.Equal(expected: first.Thumbprint, actual: second.Thumbprint);
    }

    [Fact]
    public void GetOrCreate_LegacyPasswordFile_MigratesItIntoTheStore_AndDeletesTheFile()
    {
        if (!IsWindows) return;

        // Seed a certificate + password via a throwaway store, then reconstruct the pre-Phase-3 world: the
        // password moved onto a plaintext file and removed from the store.
        var seedStore = new ProtectedSecretStore(Path.Combine(path1: _directory, path2: "seed-secrets.dat"));
        using (TelemetryTlsCertificate.GetOrCreate(certificatePath: CertificatePath, passwordPath: PasswordPath,
                   secretStore: seedStore))
        {
        }

        Assert.True(seedStore.TryRead(name: "telemetry:cert-password", value: out var seededPassword));
        seedStore.Delete("telemetry:cert-password");
        File.WriteAllText(path: PasswordPath, contents: seededPassword);

        var freshStore = new ProtectedSecretStore(SecretStorePath);
        using var certificate = TelemetryTlsCertificate.GetOrCreate(certificatePath: CertificatePath,
            passwordPath: PasswordPath, secretStore: freshStore);

        Assert.False(File.Exists(PasswordPath));
        Assert.True(freshStore.TryRead(name: "telemetry:cert-password", value: out var migratedPassword));
        Assert.Equal(expected: seededPassword, actual: migratedPassword);
    }
}