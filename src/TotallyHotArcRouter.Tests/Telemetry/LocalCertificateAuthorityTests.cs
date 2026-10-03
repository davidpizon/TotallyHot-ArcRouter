using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using LeafProfile = TotallyHot.ArcRouter.Telemetry.LocalCertificateAuthority.LeafProfile;

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

    /// <summary>The leaf's SAN extension, as the DNS names and IP addresses it lists.</summary>
    private static (string[] DnsNames, IPAddress[] IpAddresses) SubjectAlternativeNames(X509Certificate2 leaf)
    {
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        return ([.. san.EnumerateDnsNames()], [.. san.EnumerateIPAddresses()]);
    }

    /// <summary>
    /// Builds a <c>CN=localhost</c> leaf signed by <paramref name="ca"/> directly, bypassing
    /// <see cref="LocalCertificateAuthority.GetOrCreateLeaf(string)"/>'s own issuance, and persists it to
    /// <paramref name="leafPath"/> under <paramref name="passwordSecretName"/> exactly as the router would.
    /// Returns the seeded certificate, private key included.
    /// </summary>
    /// <param name="ca">The issuing CA.</param>
    /// <param name="leafPath">Where to write the leaf's PFX.</param>
    /// <param name="store">The secret store that holds the PFX password.</param>
    /// <param name="passwordSecretName">The profile's password-secret name, e.g. <c>router-leaf:cert-password</c>.</param>
    /// <param name="notAfter">The seeded leaf's expiry.</param>
    /// <param name="withAuthorityKeyIdentifier">
    /// Whether the leaf carries an AKI naming <paramref name="ca"/>. Leaves persisted before AKIs were added
    /// have none.
    /// </param>
    private static X509Certificate2 SeedLeaf(X509Certificate2 ca, string leafPath, ProtectedSecretStore store,
        string passwordSecretName, DateTimeOffset notAfter, bool withAuthorityKeyIdentifier = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName: "CN=localhost", key: rsa,
            hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);
        if (withAuthorityKeyIdentifier)
        {
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                certificate: ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        }

        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        using var signed = request.Create(issuerCertificate: ca, notBefore: DateTimeOffset.UtcNow.AddHours(-1),
            notAfter: notAfter, serialNumber: serialNumber);
        var leaf = signed.CopyWithPrivateKey(rsa);

        File.WriteAllBytes(leafPath, leaf.Export(X509ContentType.Pkcs12, "test-password"));
        store.Write(name: passwordSecretName, value: "test-password");
        return leaf;
    }

    /// <summary>The key identifier in <paramref name="leaf"/>'s Authority Key Identifier extension.</summary>
    private static byte[] AuthorityKeyId(X509Certificate2 leaf) =>
        leaf.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single().KeyIdentifier!.Value.ToArray();

    /// <summary>The key identifier in <paramref name="certificate"/>'s Subject Key Identifier extension.</summary>
    private static byte[] SubjectKeyId(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifierBytes.ToArray();

    /// <summary>
    /// Builds <paramref name="leaf"/>'s chain against <paramref name="ca"/> alone, the same way the
    /// name-constraint tests below do, and fails with the chain status when it does not build.
    /// </summary>
    private static void AssertChainsToCa(X509Certificate2 leaf, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(condition: chain.Build(leaf),
            userMessage: $"Chain build failed: {string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation))}");
    }

    /// <summary>
    /// Runs one real TLS handshake over loopback: the server picks its certificate the way the router's
    /// listeners do, by <see cref="LocalCertificateAuthority.SelectLeafProfile"/> on the client's SNI, and
    /// the client dials <paramref name="targetHost"/> trusting only <paramref name="ca"/>. Returns the
    /// certificate the client accepted; the handshake itself fails on any chain or host-name error.
    /// </summary>
    private static async Task<X509Certificate2> HandshakeAsync(string targetHost, X509Certificate2 ca,
        string directory, ProtectedSecretStore store)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var served = new List<X509Certificate2>();
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var serverStream = new SslStream(accepted.GetStream());
            await serverStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, serverName) =>
                {
                    var leaf = LocalCertificateAuthority.GetOrCreateLeaf(
                        profile: LocalCertificateAuthority.SelectLeafProfile(serverName), directory: directory,
                        secretStore: store);
                    served.Add(leaf);
                    return leaf;
                },
            }, cancellationToken);
        }, cancellationToken);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            await using var clientStream = new SslStream(client.GetStream());
            await clientStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                    CustomTrustStore = { ca },
                },
            }, cancellationToken);
            await server;

            return X509CertificateLoader.LoadCertificate(clientStream.RemoteCertificate!.GetRawCertData());
        }
        finally
        {
            foreach (var leaf in served) leaf.Dispose();
        }
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
            using var soonToExpireWithKey = SeedLeaf(ca: ca, leafPath: leafPath, store: store,
                passwordSecretName: "router-leaf:cert-password",
                notAfter: DateTimeOffset.UtcNow.AddDays(5)); // inside LeafRenewalWindow (30 days)

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetOrCreateLeaf_EveryProfile_CarriesAnAkiNamingTheCaAndItsOwnSki(bool dnsOnly)
    {
        var profile = dnsOnly ? LeafProfile.DnsOnly : LeafProfile.Loopback;
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            using var ca = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(directory, "router-ca.pfx"), secretStore: store);
            using var leaf = LocalCertificateAuthority.GetOrCreateLeaf(profile: profile, directory: directory,
                secretStore: store);

            var aki = leaf.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single();
            Assert.False(aki.Critical);
            Assert.Null(aki.NamedIssuer); // key identifier only, as BoringSSL needs
            Assert.Null(aki.SerialNumber);
            Assert.Equal(expected: SubjectKeyId(ca), actual: AuthorityKeyId(leaf));

            var ski = leaf.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single();
            Assert.False(ski.Critical);
            Assert.NotEqual(expected: SubjectKeyId(ca), actual: SubjectKeyId(leaf));
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_PersistedLeafWithoutAki_IsReissuedOnceUnderTheSameCa()
    {
        // A leaf persisted before leaves carried an AKI, nowhere near its renewal window. It must be
        // re-minted on load, under the CA already on disk (so no client re-trusts anything), and the
        // replacement must then be kept rather than re-minted on every handshake.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);
            using var legacy = SeedLeaf(ca: ca, leafPath: leafPath, store: store,
                passwordSecretName: "router-leaf:cert-password", notAfter: DateTimeOffset.UtcNow.AddDays(300),
                withAuthorityKeyIdentifier: false);

            using var reissued = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);
            using var caAfter = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);
            using var reloaded = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);

            Assert.NotEqual(expected: legacy.Thumbprint, actual: reissued.Thumbprint);
            Assert.Equal(expected: ca.Thumbprint, actual: caAfter.Thumbprint);
            Assert.Equal(expected: SubjectKeyId(ca), actual: AuthorityKeyId(reissued));
            AssertChainsToCa(leaf: reissued, ca: ca);
            Assert.Equal(expected: reissued.Thumbprint, actual: reloaded.Thumbprint);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_PersistedLeafWithAMatchingAki_IsKept()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var ca = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);
            using var seeded = SeedLeaf(ca: ca, leafPath: leafPath, store: store,
                passwordSecretName: "router-leaf:cert-password", notAfter: DateTimeOffset.UtcNow.AddDays(300));

            using var loaded = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);

            Assert.Equal(expected: seeded.Thumbprint, actual: loaded.Thumbprint);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_AfterTheCaIsReplaced_ReissuesTheLeafUnderTheNewCa()
    {
        // A missing CA file makes GetOrCreateCa mint a new CA, as an expired one would. The persisted leaf
        // is still well inside its validity, but its AKI names the old CA's key, so it must not be served.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var caPath = Path.Combine(directory, "ca.pfx");
            var leafPath = Path.Combine(directory, "leaf.pfx");

            using var oldLeaf = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);
            File.Delete(caPath);

            using var newLeaf = LocalCertificateAuthority.GetOrCreateLeaf(caCertificatePath: caPath,
                leafCertificatePath: leafPath, secretStore: store);
            using var newCa = LocalCertificateAuthority.GetOrCreateCa(certificatePath: caPath, secretStore: store);

            Assert.NotEqual(expected: oldLeaf.Thumbprint, actual: newLeaf.Thumbprint);
            Assert.Equal(expected: SubjectKeyId(newCa), actual: AuthorityKeyId(newLeaf));
            AssertChainsToCa(leaf: newLeaf, ca: newCa);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_WithTwoTrustedCasOfTheSameName_ChainsToItsOwnIssuer()
    {
        // The reinstall case: an old router CA with the same subject is still trusted next to the current
        // one. The decoy goes into the trust store first, so a builder matching on subject alone would
        // meet it first.
        var directory = TempDirectory();
        var decoyDirectory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var decoyStore = new ProtectedSecretStore(Path.Combine(decoyDirectory, "secrets.dat"));
            using var decoy = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(decoyDirectory, "ca.pfx"), secretStore: decoyStore);
            using var ca = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(directory, "ca.pfx"), secretStore: store);
            using var leaf = LocalCertificateAuthority.GetOrCreateLeaf(
                caCertificatePath: Path.Combine(directory, "ca.pfx"),
                leafCertificatePath: Path.Combine(directory, "leaf.pfx"), secretStore: store);
            Assert.Equal(expected: decoy.Subject, actual: ca.Subject);

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(decoy);
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            Assert.True(condition: chain.Build(leaf),
                userMessage: $"Chain build failed: {string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation))}");
            Assert.Equal(expected: ca.Thumbprint, actual: chain.ChainElements[1].Certificate.Thumbprint);
        }
        finally
        {
            CleanUp(directory);
            CleanUp(decoyDirectory);
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
            using var soonToExpireWithKey = SeedLeaf(ca: ca, leafPath: leafPath, store: store,
                passwordSecretName: "router-leaf:cert-password",
                notAfter: DateTimeOffset.UtcNow.AddDays(5)); // inside LeafRenewalWindow (30 days)

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

    [Fact]
    public void GetOrCreateLeaf_DnsOnlyProfile_CarriesNoIpAddress()
    {
        // The regression guard for ADR-0013 Amendment 1. BoringSSL (Bun, so the native Claude Code build)
        // has no IP-address name-constraint support: an IP SAN under this CA's IP subtrees fails the whole
        // chain with X509_V_ERR_UNSUPPORTED_CONSTRAINT_TYPE. The DnsOnly leaf is what every SNI handshake
        // gets, so a single IP SAN added here breaks Claude Code on every HTTPS port.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));

            using var leaf = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.DnsOnly,
                directory: directory, secretStore: store);
            using var ca = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(directory, "router-ca.pfx"), secretStore: store);

            var (dnsNames, ipAddresses) = SubjectAlternativeNames(leaf);
            Assert.Equal(expected: new[] { "localhost" }, actual: dnsNames);
            Assert.True(condition: ipAddresses.Length == 0,
                userMessage: $"The DnsOnly leaf must carry no IP SAN (BoringSSL rejects it), but carries: {string.Join(", ", ipAddresses.Select(a => a.ToString()))}");
            Assert.Equal(expected: ca.Subject, actual: leaf.Issuer);
            AssertChainsToCa(leaf: leaf, ca: ca);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Fact]
    public void GetOrCreateLeaf_LoopbackProfile_KeepsBothLoopbackAddresses()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));

            using var leaf = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.Loopback,
                directory: directory, secretStore: store);

            var (dnsNames, ipAddresses) = SubjectAlternativeNames(leaf);
            Assert.Equal(expected: new[] { "localhost" }, actual: dnsNames);
            Assert.Equal(expected: new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, actual: ipAddresses);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    public void SelectLeafProfile_PicksDnsOnlyExactlyWhenTheClientNamedAHost(string? serverName,
        bool expectDnsOnly)
    {
        var expected = expectDnsOnly ? LeafProfile.DnsOnly : LeafProfile.Loopback;
        Assert.Equal(expected: expected, actual: LocalCertificateAuthority.SelectLeafProfile(serverName));
    }

    [Fact]
    public void GetOrCreateLeaf_Profiles_PersistSeparatelyAndRenewIndependently()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));

            using var loopback = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.Loopback,
                directory: directory, secretStore: store);
            using var dnsOnly = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.DnsOnly,
                directory: directory, secretStore: store);

            Assert.NotEqual(expected: loopback.Thumbprint, actual: dnsOnly.Thumbprint);
            Assert.True(File.Exists(Path.Combine(directory, "router-leaf.pfx")));
            Assert.True(File.Exists(Path.Combine(directory, "router-leaf-dns.pfx")));

            using (var dnsOnlyAgain = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.DnsOnly,
                       directory: directory, secretStore: store))
            {
                Assert.Equal(expected: dnsOnly.Thumbprint, actual: dnsOnlyAgain.Thumbprint);
            }

            // Push only the DnsOnly leaf into its renewal window (same technique as
            // GetOrCreateLeaf_NearExpiry_IssuesAReplacement); the Loopback leaf must not be re-minted.
            using var ca = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(directory, "router-ca.pfx"), secretStore: store);
            using var soonToExpireWithKey = SeedLeaf(ca: ca, leafPath: Path.Combine(directory, "router-leaf-dns.pfx"),
                store: store, passwordSecretName: "router-leaf-dns:cert-password",
                notAfter: DateTimeOffset.UtcNow.AddDays(5));

            using var renewedDnsOnly = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.DnsOnly,
                directory: directory, secretStore: store);
            using var loopbackAgain = LocalCertificateAuthority.GetOrCreateLeaf(profile: LeafProfile.Loopback,
                directory: directory, secretStore: store);

            Assert.NotEqual(expected: soonToExpireWithKey.Thumbprint, actual: renewedDnsOnly.Thumbprint);
            Assert.Empty(SubjectAlternativeNames(renewedDnsOnly).IpAddresses);
            Assert.Equal(expected: loopback.Thumbprint, actual: loopbackAgain.Thumbprint);
        }
        finally
        {
            CleanUp(directory);
        }
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", true)]
    public async Task Handshake_PicksTheLeafByTheClientsSni(string targetHost, bool expectIpAddresses)
    {
        // A real TLS handshake over loopback, so the SNI that reaches the selector is whatever the client
        // stack actually sends: a host name for "localhost", nothing for an IP literal. Both handshakes
        // validate the chain and the host name against the CA alone.
        var directory = TempDirectory();
        try
        {
            var store = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            using var ca = LocalCertificateAuthority.GetOrCreateCa(
                certificatePath: Path.Combine(directory, "router-ca.pfx"), secretStore: store);

            using var presented = await HandshakeAsync(targetHost: targetHost, ca: ca, directory: directory,
                store: store);

            Assert.Equal(expected: expectIpAddresses, actual: SubjectAlternativeNames(presented).IpAddresses.Length > 0);
        }
        finally
        {
            CleanUp(directory);
        }
    }
}
