using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="LocalCertificateAuthority"/>: CA/leaf generation and persistence, renewal, and -
/// the part worth the most scrutiny - that the hand-encoded RFC 5280 <c>NameConstraints</c> extension
/// (ADR-0013) actually round-trips through .NET's own ASN.1 parser and is genuinely enforced by
/// <see cref="X509Chain"/>. None of this touches any OS trust store - <see cref="X509ChainTrustMode.CustomRootTrust"/>
/// builds a chain against an in-memory anchor only, which is what makes this a real, offline test of the
/// extension's correctness rather than a reasoned-about assumption.
/// </summary>
public sealed class LocalCertificateAuthorityTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanUp(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void GetOrCreateCa_NoExistingFile_GeneratesAConstrainedCa()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));

            using var ca = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(directory, "ca.pfx"), secretStore: store);

            Assert.Equal(expected: LocalCertificateAuthority.CaSubjectName, actual: ca.Subject);
            Assert.True(ca.HasPrivateKey);
            var basicConstraints = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
            Assert.True(basicConstraints.CertificateAuthority);
            Assert.True(basicConstraints.HasPathLengthConstraint);
            Assert.Equal(expected: 0, actual: basicConstraints.PathLengthConstraint);
            Assert.Contains(ca.Extensions, e => e.Oid?.Value == "2.5.29.30"); // NameConstraints
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateCa_CalledTwice_ReturnsTheSamePersistedCa()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var path = Path.Combine(directory, "ca.pfx");

            using var first = LocalCertificateAuthority.GetOrCreateCa(certificatePath: path, secretStore: store);
            using var second = LocalCertificateAuthority.GetOrCreateCa(certificatePath: path, secretStore: store);

            Assert.Equal(expected: first.Thumbprint, actual: second.Thumbprint);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_IsSignedByTheCa()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);
            using var leaf = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);

            Assert.Equal(expected: "CN=localhost", actual: leaf.Subject);
            Assert.Equal(expected: ca.Subject, actual: leaf.Issuer);
            Assert.True(leaf.HasPrivateKey);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_CalledTwice_ReturnsTheSamePersistedLeaf()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var first = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);
            using var second = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);

            Assert.Equal(expected: first.Thumbprint, actual: second.Thumbprint);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_NearExpiry_IssuesAReplacement()
    {
        // Builds a leaf directly (bypassing GetOrCreateLeaf's own issuance) that is already inside the
        // renewal window, persists it exactly as GetOrCreateLeaf would, then confirms the next call
        // mints a genuinely different certificate rather than returning the stale one.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(subjectName: "CN=localhost", key: rsa,
                hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);
            var serialNumber = new byte[16];
            RandomNumberGenerator.Fill(serialNumber);
            using var soonToExpire = request.Create(issuerCertificate: ca,
                notBefore: DateTimeOffset.UtcNow.AddHours(-1),
                notAfter: DateTimeOffset.UtcNow.AddDays(5), // inside LeafRenewalWindow (30 days)
                serialNumber: serialNumber);
            using var soonToExpireWithKey = soonToExpire.CopyWithPrivateKey(rsa);

            var password = "test-password";
            File.WriteAllBytes(leafPath,
                soonToExpireWithKey.Export(X509ContentType.Pkcs12, password));
            store.Write(name: "router-leaf:cert-password", value: password);

            using var renewed = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);

            Assert.NotEqual(expected: soonToExpireWithKey.Thumbprint, actual: renewed.Thumbprint);
            Assert.True(renewed.NotAfter > DateTime.UtcNow.AddDays(300));
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public async Task GetOrCreateLeaf_ConcurrentCallsDuringRenewal_AllReturnTheSameCertificate()
    {
        // Regression coverage for a real bug: GetOrCreateLeaf() is called from a ServerCertificateSelector
        // on every TLS handshake, across every listener in the process, with no cache in front of it -
        // several simultaneous handshakes can all decide "time to renew" at the same moment. Without
        // RenewalLock serializing the whole check-renew-persist sequence, each thread would mint its own
        // competing cert/password pair and their file-rename/secret-store-write steps could interleave,
        // leaving the on-disk PFX paired with a different renewal's password than the one actually there
        // - the next handshake to load it would then fail outright. Seeds a leaf already inside the
        // renewal window (same technique as GetOrCreateLeaf_NearExpiry_IssuesAReplacement) so every
        // concurrent caller below genuinely attempts a renewal, not just a cache hit.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(subjectName: "CN=localhost", key: rsa,
                hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);
            var serialNumber = new byte[16];
            RandomNumberGenerator.Fill(serialNumber);
            using var soonToExpire = request.Create(issuerCertificate: ca,
                notBefore: DateTimeOffset.UtcNow.AddHours(-1),
                notAfter: DateTimeOffset.UtcNow.AddDays(5), // inside LeafRenewalWindow (30 days)
                serialNumber: serialNumber);
            using var soonToExpireWithKey = soonToExpire.CopyWithPrivateKey(rsa);

            File.WriteAllBytes(leafPath, soonToExpireWithKey.Export(X509ContentType.Pkcs12, "test-password"));
            store.Write(name: "router-leaf:cert-password", value: "test-password");

            var tasks = Enumerable.Range(0, 12)
                .Select(_ => Task.Run(() => LocalCertificateAuthority.GetOrCreateLeaf(
                    caCertificatePath: caPath, leafCertificatePath: leafPath, secretStore: store)))
                .ToArray();
            var renewed = await Task.WhenAll(tasks);

            try
            {
                var thumbprints = renewed.Select(cert => cert.Thumbprint).Distinct().ToList();
                Assert.Single(thumbprints);
                Assert.NotEqual(expected: soonToExpireWithKey.Thumbprint, actual: thumbprints[0]);

                // The strongest check: load straight from what's actually on disk, exactly as the next
                // real TLS handshake would - this is what a torn cert/password pair would fail.
                using var reloaded = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                    leafCertificatePath: leafPath, secretStore: store);
                Assert.Equal(expected: thumbprints[0], actual: reloaded.Thumbprint);
            }
            finally
            {
                foreach (var cert in renewed) cert.Dispose();
            }
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void NameConstraints_AcceptsALocalhostLeaf_ViaCustomRootTrust()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);
            using var leaf = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreInvalidBasicConstraints;

            var built = chain.Build(leaf);

            Assert.True(condition: built,
                userMessage: $"Chain build failed: {string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation))}");
            Assert.DoesNotContain(chain.ChainStatus,
                s => s.Status is X509ChainStatusFlags.InvalidNameConstraints
                    or X509ChainStatusFlags.HasNotSupportedNameConstraint
                    or X509ChainStatusFlags.HasNotDefinedNameConstraint
                    or X509ChainStatusFlags.HasNotPermittedNameConstraint
                    or X509ChainStatusFlags.HasExcludedNameConstraint);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void NameConstraints_RejectsALeafForAnUnrelatedName_ViaCustomRootTrust()
    {
        // The actual security property ADR-0013 depends on: a leaf this CA signs for any name outside
        // localhost/127.0.0.1/::1 must fail chain validation against it, proving the NameConstraints
        // extension this CA carries is both round-tripping through .NET's ASN.1 parser and being
        // enforced, not merely present as inert bytes.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(subjectName: "CN=evil.example", key: rsa,
                hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("evil.example");
            request.CertificateExtensions.Add(sanBuilder.Build());
            var serialNumber = new byte[16];
            RandomNumberGenerator.Fill(serialNumber);
            using var rogueLeaf = request.Create(issuerCertificate: ca,
                notBefore: DateTimeOffset.UtcNow.AddDays(-1), notAfter: DateTimeOffset.UtcNow.AddDays(30),
                serialNumber: serialNumber);
            using var rogueLeafWithKey = rogueLeaf.CopyWithPrivateKey(rsa);

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreInvalidBasicConstraints;

            var built = chain.Build(rogueLeafWithKey);

            Assert.False(built);
            Assert.Contains(chain.ChainStatus,
                s => s.Status is X509ChainStatusFlags.InvalidNameConstraints
                    or X509ChainStatusFlags.HasNotSupportedNameConstraint
                    or X509ChainStatusFlags.HasNotDefinedNameConstraint
                    or X509ChainStatusFlags.HasNotPermittedNameConstraint
                    or X509ChainStatusFlags.HasExcludedNameConstraint);
        }
        finally
        {
            CleanUp(directory);
        }
    }
}
