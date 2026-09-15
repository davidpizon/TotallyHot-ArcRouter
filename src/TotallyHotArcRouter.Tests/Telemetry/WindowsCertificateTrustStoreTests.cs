using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="WindowsCertificateTrustStore"/>'s add/find-by-thumbprint/remove mechanics against a
/// deliberately harmless store (<see cref="StoreLocation.CurrentUser"/>/<see cref="StoreName.My"/>) -
/// never <see cref="StoreLocation.LocalMachine"/>/<see cref="StoreName.Root"/>, the real trust anchor
/// this class defaults to in production. This is real verification of the X509Store calls, not a mock,
/// without ever touching this machine's actual trusted-root list - the one thing this test suite must
/// never do (see AGENTS.md's action-with-care guidance on system/security settings).
/// </summary>
public sealed class WindowsCertificateTrustStoreTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static X509Certificate2 CreateSelfSignedTestCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(subjectName: "CN=arcrouter-test-cert", key: rsa,
            hashAlgorithm: System.Security.Cryptography.HashAlgorithmName.SHA256,
            padding: System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(notBefore: DateTimeOffset.UtcNow.AddDays(-1),
            notAfter: DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            data: created.Export(X509ContentType.Pkcs12), password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public void Install_AddsTheCertificateToTheStore()
    {
        if (!IsWindows) return;

        using var certificate = CreateSelfSignedTestCertificate();
        var store = new WindowsCertificateTrustStore(name: StoreName.My, location: StoreLocation.CurrentUser);
        try
        {
            store.Install(certificate);

            using var underlyingStore = new X509Store(storeName: StoreName.My, storeLocation: StoreLocation.CurrentUser);
            underlyingStore.Open(OpenFlags.ReadOnly);
            var found = underlyingStore.Certificates.Find(findType: X509FindType.FindByThumbprint,
                findValue: certificate.Thumbprint, validOnly: false);

            Assert.Single(found);
        }
        finally
        {
            store.Uninstall(certificate);
        }
    }

    [Fact]
    public void Install_CalledTwice_DoesNotDuplicate()
    {
        if (!IsWindows) return;

        using var certificate = CreateSelfSignedTestCertificate();
        var store = new WindowsCertificateTrustStore(name: StoreName.My, location: StoreLocation.CurrentUser);
        try
        {
            store.Install(certificate);
            store.Install(certificate);

            using var underlyingStore = new X509Store(storeName: StoreName.My, storeLocation: StoreLocation.CurrentUser);
            underlyingStore.Open(OpenFlags.ReadOnly);
            var found = underlyingStore.Certificates.Find(findType: X509FindType.FindByThumbprint,
                findValue: certificate.Thumbprint, validOnly: false);

            Assert.Single(found);
        }
        finally
        {
            store.Uninstall(certificate);
        }
    }

    [Fact]
    public void Uninstall_RemovesTheCertificate()
    {
        if (!IsWindows) return;

        using var certificate = CreateSelfSignedTestCertificate();
        var store = new WindowsCertificateTrustStore(name: StoreName.My, location: StoreLocation.CurrentUser);
        store.Install(certificate);

        store.Uninstall(certificate);

        using var underlyingStore = new X509Store(storeName: StoreName.My, storeLocation: StoreLocation.CurrentUser);
        underlyingStore.Open(OpenFlags.ReadOnly);
        var found = underlyingStore.Certificates.Find(findType: X509FindType.FindByThumbprint,
            findValue: certificate.Thumbprint, validOnly: false);

        Assert.Empty(found);
    }

    [Fact]
    public void Uninstall_NoMatchingCertificate_DoesNotThrow()
    {
        if (!IsWindows) return;

        using var certificate = CreateSelfSignedTestCertificate();
        var store = new WindowsCertificateTrustStore(name: StoreName.My, location: StoreLocation.CurrentUser);

        var act = () => store.Uninstall(certificate);

        var exception = Record.Exception(act);
        Assert.Null(exception);
    }
}
