using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Generates, persists, and rotates the router's own name-constrained local CA and the leaf certificate
/// every TLS listener (the web port, MCP, and - as of the web GUI migration plan's Phase P7 - the LLM
/// proxy port) presents (ADR-0013). Supersedes <see cref="TelemetryTlsCertificate"/>'s single long-lived
/// self-signed leaf: a leaf issued under a locally-trusted CA can be silently rotated (see
/// <see cref="GetOrCreateLeaf()"/>'s remarks) without ever asking an already-trusting client to re-trust
/// anything, which a self-signed leaf cannot do without repeating the OS-trust step on every renewal.
/// </summary>
/// <remarks>
/// <para>
/// The CA is <c>pathLen:0</c> (it may sign leaves, never intermediate CAs) and carries an RFC 5280
/// <c>NameConstraints</c> extension permitting only <c>localhost</c>, <c>127.0.0.1</c>, and <c>::1</c> -
/// a compromised CA private key can therefore never be used to mint a certificate any client would
/// accept for an unrelated hostname. .NET has no high-level builder for this extension (unlike
/// <see cref="X509BasicConstraintsExtension"/>), so it is hand-encoded via <see cref="AsnWriter"/>
/// following RFC 5280 §4.2.1.10 - see <see cref="BuildNameConstraintsExtension"/>. This is verified for
/// real (not merely reasoned about) by <c>LocalCertificateAuthorityTests</c>: it builds an
/// <see cref="X509Chain"/> with the CA as a <see cref="X509ChainTrustMode.CustomRootTrust"/> anchor - no
/// OS trust store involved - and confirms .NET's own chain-validation engine both accepts a
/// <c>localhost</c> leaf and rejects one for an unrelated name, which is only possible if the extension
/// round-trips correctly through .NET's own ASN.1 parser.
/// </para>
/// <para>
/// Both the CA and the leaf are persisted the same way <see cref="TelemetryTlsCertificate"/> persists its
/// certificate: a password-protected <c>.pfx</c> under the machine-shared data directory
/// (<see cref="AppDataPaths"/>), with the random per-installation password held in
/// <see cref="ProtectedSecretStore"/>. The CA's key is the higher-value secret of the two (ADR-0013) -
/// its compromise lets an attacker mint a certificate any already-trusting client on this machine would
/// accept for the router's own loopback identities - but it uses the same protector every other secret
/// in this application does, rather than a bespoke mechanism, on the same reasoning
/// <c>docs/router/secrets-at-rest.md</c> already applies elsewhere: one hardened store is easier to
/// reason about and keep correct than several.
/// </para>
/// </remarks>
public static class LocalCertificateAuthority
{
    private const string CaCertificateFileName = "router-ca.pfx";
    private const string CaPasswordSecretName = "router-ca:cert-password";
    private const string LeafCertificateFileName = "router-leaf.pfx";
    private const string LeafPasswordSecretName = "router-leaf:cert-password";

    /// <summary>The CA's subject/issuer name, distinguishing it from the leaves it signs (both of which use <c>CN=localhost</c>).</summary>
    public const string CaSubjectName = "CN=TotallyHot Arc Router Local CA";

    /// <summary>
    /// How long a freshly issued leaf is valid for: 397 days, the CA/Browser Forum's maximum permitted
    /// publicly-trusted leaf lifetime. This CA is never publicly trusted, but there is no reason to
    /// exceed a limit every mainstream browser already enforces.
    /// </summary>
    public static readonly TimeSpan LeafValidity = TimeSpan.FromDays(397);

    /// <summary>
    /// How long before expiry <see cref="GetOrCreateLeaf()"/> mints a replacement rather than returning
    /// the cached leaf. Wide enough that an operator who starts the router only occasionally still
    /// renews well ahead of expiry, without needing a background timer - every call re-checks.
    /// </summary>
    public static readonly TimeSpan LeafRenewalWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// How long the CA itself is valid for. Long-lived by design (ADR-0013's whole point is that
    /// re-trust should be rare): a leaf rotation never requires re-trusting the CA, so the CA only
    /// needs renewing on a timescale roughly matching "how long before someone reinstalls the machine
    /// anyway", not a leaf's.
    /// </summary>
    public static readonly TimeSpan CaValidity = TimeSpan.FromDays(3650);

    /// <summary>
    /// Loads the persisted CA if one already exists, otherwise generates a new self-signed,
    /// name-constrained one and persists it before returning it.
    /// </summary>
    public static X509Certificate2 GetOrCreateCa()
    {
        var directory = AppDataPaths.ResolveMachineSharedDirectory();
        return GetOrCreateCa(
            certificatePath: Path.Combine(path1: directory, path2: CaCertificateFileName),
            secretStore: new ProtectedSecretStore());
    }

    /// <summary>
    /// Loads the persisted leaf if one already exists and is not within its renewal window, otherwise
    /// issues a fresh one under <see cref="GetOrCreateCa()"/> and persists it before returning it.
    /// </summary>
    /// <remarks>
    /// Safe to call on every TLS handshake (see <c>ProxyServer</c>'s <c>ServerCertificateSelector</c>
    /// wiring) - the common case is a single file-existence-and-expiry check, and renewal itself is rare
    /// enough (at most once every <see cref="LeafValidity"/> minus <see cref="LeafRenewalWindow"/>) that
    /// doing it inline, without a background timer, is simpler and cannot drift out of sync with what a
    /// handshake actually presents.
    /// </remarks>
    public static X509Certificate2 GetOrCreateLeaf()
    {
        var directory = AppDataPaths.ResolveMachineSharedDirectory();
        return GetOrCreateLeaf(
            caCertificatePath: Path.Combine(path1: directory, path2: CaCertificateFileName),
            leafCertificatePath: Path.Combine(path1: directory, path2: LeafCertificateFileName),
            secretStore: new ProtectedSecretStore());
    }

    /// <summary>Overload taking explicit paths and a secret store, for tests. See <see cref="GetOrCreateCa()"/> for behavior.</summary>
    internal static X509Certificate2 GetOrCreateCa(string certificatePath, ProtectedSecretStore secretStore)
    {
        var directory = Path.GetDirectoryName(certificatePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(certificatePath) &&
            secretStore.TryRead(name: CaPasswordSecretName, value: out var existingPassword))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path: certificatePath, password: existingPassword,
                keyStorageFlags: X509KeyStorageFlags.Exportable);
            if (existing.NotAfter > DateTime.UtcNow) return existing;

            // An expired CA cannot be extended in place - fall through and mint a fresh one. Every leaf
            // it ever signed is now untrusted too, but that is unavoidable: a 10-year CaValidity means
            // this only happens if the machine has been running the same install for a decade.
            existing.Dispose();
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(subjectName: CaSubjectName, key: rsa,
            hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            keyUsages: X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(BuildNameConstraintsExtension());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var certificate = request.CreateSelfSigned(
            notBefore: DateTimeOffset.UtcNow.AddDays(-1),
            notAfter: DateTimeOffset.UtcNow.Add(CaValidity));

        return PersistAndReload(certificate: certificate, certificatePath: certificatePath,
            secretStore: secretStore, passwordSecretName: CaPasswordSecretName);
    }

    /// <summary>Overload taking explicit paths and a secret store, for tests. See <see cref="GetOrCreateLeaf()"/> for behavior.</summary>
    internal static X509Certificate2 GetOrCreateLeaf(string caCertificatePath, string leafCertificatePath,
        ProtectedSecretStore secretStore)
    {
        using var ca = GetOrCreateCa(certificatePath: caCertificatePath, secretStore: secretStore);

        var directory = Path.GetDirectoryName(leafCertificatePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(leafCertificatePath) &&
            secretStore.TryRead(name: LeafPasswordSecretName, value: out var existingPassword))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path: leafCertificatePath, password: existingPassword,
                keyStorageFlags: X509KeyStorageFlags.Exportable);
            if (existing.NotAfter > DateTime.UtcNow.Add(LeafRenewalWindow)) return existing;

            existing.Dispose();
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName: "CN=localhost", key: rsa,
            hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            keyUsages: X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(enhancedKeyUsages: [new Oid("1.3.6.1.5.5.7.3.1")],
                false)); // Server Authentication

        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);

        using var signed = request.Create(
            issuerCertificate: ca,
            notBefore: DateTimeOffset.UtcNow.AddDays(-1),
            notAfter: DateTimeOffset.UtcNow.Add(LeafValidity),
            serialNumber: serialNumber);
        using var leaf = signed.CopyWithPrivateKey(rsa);

        return PersistAndReload(certificate: leaf, certificatePath: leafCertificatePath,
            secretStore: secretStore, passwordSecretName: LeafPasswordSecretName);
    }

    /// <summary>
    /// Exports <paramref name="certificate"/> (private key included) to <paramref name="certificatePath"/>
    /// under a fresh random password stored in <paramref name="secretStore"/>, then reloads and returns it
    /// from the written bytes - the same load-after-write shape <see cref="TelemetryTlsCertificate"/> uses,
    /// so the returned instance's key storage flags are consistent regardless of whether this call created
    /// or loaded the certificate.
    /// </summary>
    /// <remarks>
    /// Ordered so a failure never leaves an existing on-disk certificate paired with the wrong password
    /// in the store - the pairing a caller like <see cref="GetOrCreateCa(string, ProtectedSecretStore)"/>
    /// relies on to load an existing certificate at all. The new PFX bytes are written to a sibling temp
    /// file first (never touching <paramref name="certificatePath"/> itself), the password is persisted
    /// to <paramref name="secretStore"/> second, and only once that succeeds does the temp file replace
    /// the real one via <see cref="File.Move(string, string, bool)"/> - a fast, low-failure-probability
    /// local rename, unlike the store write it follows. If the store write throws (an unwritable key
    /// ring, say), the temp file is deleted and the exception propagates with the existing
    /// certificate/password pair on disk untouched and still loadable, rather than a PFX already
    /// overwritten under a password the store never ended up holding.
    /// </remarks>
    private static X509Certificate2 PersistAndReload(X509Certificate2 certificate, string certificatePath,
        ProtectedSecretStore secretStore, string passwordSecretName)
    {
        var password = Guid.NewGuid().ToString("N");
        var certificateBytes = certificate.Export(contentType: X509ContentType.Pkcs12, password: password);

        var tempPath = $"{certificatePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(path: tempPath, bytes: certificateBytes);
        try
        {
            secretStore.Write(name: passwordSecretName, value: password);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }

        File.Move(sourceFileName: tempPath, destFileName: certificatePath, overwrite: true);

        return X509CertificateLoader.LoadPkcs12(data: certificateBytes, password: password,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Builds the RFC 5280 §4.2.1.10 <c>NameConstraints</c> extension (OID <c>2.5.29.30</c>) permitting
    /// only <c>localhost</c> (DNS), <c>127.0.0.1</c>, and <c>::1</c> (each as a single-address IP subtree -
    /// a /32 or /128 mask) - the local CA's entire reason for existing over a broader-scoped one
    /// (ADR-0013). Hand-encoded via <see cref="AsnWriter"/> because .NET provides no builder for this
    /// extension, unlike <see cref="X509BasicConstraintsExtension"/> or
    /// <see cref="SubjectAlternativeNameBuilder"/>.
    /// </summary>
    internal static X509Extension BuildNameConstraintsExtension()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        // NameConstraints ::= SEQUENCE { permittedSubtrees [0] GeneralSubtrees OPTIONAL, ... }
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteDnsSubtree(writer, "localhost");
                WriteIpSubtree(writer, IPAddress.Loopback, IPAddress.Parse("255.255.255.255"));
                WriteIpSubtree(writer, IPAddress.IPv6Loopback, IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"));
            }
        }

        return new X509Extension(oid: "2.5.29.30", rawData: writer.Encode(), critical: true);
    }

    /// <summary>Writes one <c>GeneralSubtree</c> whose <c>base</c> is a <c>dNSName [2]</c> <c>GeneralName</c>.</summary>
    private static void WriteDnsSubtree(AsnWriter writer, string dnsName)
    {
        using (writer.PushSequence())
        {
            writer.WriteCharacterString(encodingType: UniversalTagNumber.IA5String, str: dnsName,
                tag: new Asn1Tag(TagClass.ContextSpecific, 2));
        }
    }

    /// <summary>
    /// Writes one <c>GeneralSubtree</c> whose <c>base</c> is an <c>iPAddress [7]</c> <c>GeneralName</c>
    /// - an address/netmask pair per RFC 5280 §4.2.1.10, sized for whichever address family
    /// <paramref name="address"/> is in (4 bytes + 4 bytes for IPv4, 16 + 16 for IPv6).
    /// </summary>
    private static void WriteIpSubtree(AsnWriter writer, IPAddress address, IPAddress mask)
    {
        using (writer.PushSequence())
        {
            var bytes = new byte[address.GetAddressBytes().Length + mask.GetAddressBytes().Length];
            address.GetAddressBytes().CopyTo(bytes, 0);
            mask.GetAddressBytes().CopyTo(bytes, address.GetAddressBytes().Length);
            writer.WriteOctetString(value: bytes, tag: new Asn1Tag(TagClass.ContextSpecific, 7));
        }
    }
}
