using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Generates, persists, and rotates the router's own name-constrained local CA and the leaf certificates
/// every TLS listener (the web port, MCP, and - as of the web GUI migration plan's Phase P7 - the LLM
/// proxy port) presents (ADR-0013). A leaf issued under a locally-trusted CA can be silently rotated (see
/// <see cref="GetOrCreateLeaf(string)"/>'s remarks) without ever asking an already-trusting client to
/// re-trust anything, which a self-signed leaf cannot do without repeating the OS-trust step on every
/// renewal.
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
/// There are two leaves, one per <see cref="LeafProfile"/>, chosen per handshake by the client's SNI
/// (ADR-0013 Amendment 1). BoringSSL - the TLS library inside Bun, and so inside the native Claude Code
/// build - implements no IP-address name constraints: a leaf carrying an IP-address SAN under this CA's
/// IP-address subtrees fails there with <c>X509_V_ERR_UNSUPPORTED_CONSTRAINT_TYPE</c>, whichever host
/// the client dialled. A client that dials <c>localhost</c> sends SNI and gets the
/// <see cref="LeafProfile.DnsOnly"/> leaf, which BoringSSL accepts; a client that dials an IP literal
/// sends no SNI and gets the <see cref="LeafProfile.Loopback"/> leaf, the only one whose names match an
/// IP-literal URL. The CA itself is the same for both, so the split needs no re-trust.
/// </para>
/// <para>
/// The CA and both leaves are persisted as a password-protected <c>.pfx</c> under the machine-shared
/// data directory (<see cref="AppDataPaths"/>), with the random per-installation password held in
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
    private const string DnsOnlyLeafCertificateFileName = "router-leaf-dns.pfx";
    private const string DnsOnlyLeafPasswordSecretName = "router-leaf-dns:cert-password";

    // Guards the whole check-renew-persist sequence in both GetOrCreateCa and GetOrCreateLeaf. Real gap
    // this closes: GetOrCreateLeaf() is called from a ServerCertificateSelector on every TLS handshake,
    // across every listener (proxy, web, MCP - each its own Kestrel instance/process), with no cache in
    // front of it by design (see GetOrCreateLeaf()'s own remarks on why). PersistAndReload's
    // temp-file-then-rename-then-persist-password ordering (see its own remarks) makes a SINGLE renewal
    // safe against a mid-write failure, but says nothing about TWO concurrent renewals - which is
    // exactly what happens once the cached leaf enters its renewal window and several simultaneous
    // handshakes all decide independently "time to renew": each computes its own cert+password pair, and
    // without serialization their file-rename and secret-store-write steps can interleave, leaving the
    // on-disk PFX paired with a DIFFERENT renewal's password than the one actually on disk. A `Lock`, not
    // a `Mutex`, is deliberately in-process only - concurrent handshakes across ProxyServer's own
    // multiple listeners inside this one process are the actual race; ProtectedSecretStore's own named
    // Mutex already covers true cross-process contention for the store half alone, but not the paired
    // file-write this lock protects end to end.
    private static readonly Lock RenewalLock = new();

    /// <summary>The CA's subject/issuer name, distinguishing it from the leaves it signs (both of which use <c>CN=localhost</c>).</summary>
    public const string CaSubjectName = "CN=TotallyHot Arc Router Local CA";

    /// <summary>
    /// How long a freshly issued leaf is valid for: 397 days, the CA/Browser Forum's maximum permitted
    /// publicly-trusted leaf lifetime. This CA is never publicly trusted, but there is no reason to
    /// exceed a limit every mainstream browser already enforces.
    /// </summary>
    private static readonly TimeSpan LeafValidity = TimeSpan.FromDays(397);

    /// <summary>
    /// How long before expiry <see cref="GetOrCreateLeaf()"/> mints a replacement rather than returning
    /// the cached leaf. Wide enough that an operator who starts the router only occasionally still
    /// renews well ahead of expiry, without needing a background timer - every call re-checks.
    /// </summary>
    private static readonly TimeSpan LeafRenewalWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// How long the CA itself is valid for. Long-lived by design (ADR-0013's whole point is that
    /// re-trust should be rare): a leaf rotation never requires re-trusting the CA, so the CA only
    /// needs renewing on a timescale roughly matching "how long before someone reinstalls the machine
    /// anyway", not a leaf's.
    /// </summary>
    private static readonly TimeSpan CaValidity = TimeSpan.FromDays(3650);

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
    /// Loads or issues the <see cref="LeafProfile.Loopback"/> leaf, the one a handshake without SNI gets.
    /// Equivalent to <see cref="GetOrCreateLeaf(string)"/> with no server name.
    /// </summary>
    public static X509Certificate2 GetOrCreateLeaf() => GetOrCreateLeaf(serverName: null);

    /// <summary>
    /// Loads the persisted leaf for <paramref name="serverName"/>'s <see cref="LeafProfile"/> (see
    /// <see cref="SelectLeafProfile"/>) if one already exists, is not within its renewal window, and has an
    /// Authority Key Identifier naming the current CA's key; otherwise issues a fresh one under
    /// <see cref="GetOrCreateCa()"/> and persists it before returning it.
    /// </summary>
    /// <param name="serverName">
    /// The SNI host name from the client's TLS ClientHello, exactly as Kestrel's
    /// <c>ServerCertificateSelector</c> passes it - <see langword="null"/> or empty when the client sent
    /// none, which is what an IP-literal URL produces.
    /// </param>
    /// <remarks>
    /// Safe to call on every TLS handshake, concurrently, across every listener in this process (see
    /// <c>ProxyServer</c>'s <c>ServerCertificateSelector</c> wiring) - the common case is a single
    /// file-existence-and-expiry check serialized behind an in-process <c>Lock</c>, and renewal itself is
    /// rare enough (at most once every <see cref="LeafValidity"/> minus <see cref="LeafRenewalWindow"/>)
    /// that doing it inline, without a background timer, is simpler and cannot drift out of sync with
    /// what a handshake actually presents. The lock exists specifically because renewal is not: without
    /// it, several simultaneous handshakes deciding "time to renew" at the same moment could each persist
    /// their own cert/password pair with the writes interleaved, leaving the file on disk paired with a
    /// different renewal's password than the one that's actually there.
    /// </remarks>
    public static X509Certificate2 GetOrCreateLeaf(string? serverName) =>
        GetOrCreateLeaf(profile: SelectLeafProfile(serverName),
            directory: AppDataPaths.ResolveMachineSharedDirectory(), secretStore: new ProtectedSecretStore());

    /// <summary>
    /// Loads or issues every <see cref="LeafProfile"/>'s leaf once and disposes it, so a listener's
    /// startup fails loudly when issuance is broken (an unwritable data directory, say), rather than the
    /// first handshake to need a not-yet-issued profile failing later.
    /// </summary>
    public static void EnsureLeaves()
    {
        var directory = AppDataPaths.ResolveMachineSharedDirectory();
        var secretStore = new ProtectedSecretStore();
        foreach (var profile in Enum.GetValues<LeafProfile>())
        {
            using var leaf = GetOrCreateLeaf(profile: profile, directory: directory, secretStore: secretStore);
        }
    }

    /// <summary>
    /// Picks the leaf profile for a handshake from its SNI host name: <see cref="LeafProfile.DnsOnly"/>
    /// when the client named a host, <see cref="LeafProfile.Loopback"/> when it named none.
    /// </summary>
    /// <remarks>
    /// RFC 6066 forbids an IP literal in SNI, but a non-conforming client that sends one anyway still
    /// gets <see cref="LeafProfile.Loopback"/>, the only leaf whose names include that address.
    /// </remarks>
    /// <param name="serverName">The SNI host name, or <see langword="null"/>/empty when the client sent none.</param>
    internal static LeafProfile SelectLeafProfile(string? serverName) =>
        string.IsNullOrEmpty(serverName) || IPAddress.TryParse(serverName, out _)
            ? LeafProfile.Loopback
            : LeafProfile.DnsOnly;

    /// <summary>
    /// Overload taking a data directory and a secret store, for tests: resolves <paramref name="profile"/>'s
    /// leaf file inside <paramref name="directory"/>, next to the CA. See <see cref="GetOrCreateLeaf(string)"/>
    /// for behavior.
    /// </summary>
    internal static X509Certificate2 GetOrCreateLeaf(LeafProfile profile, string directory,
        ProtectedSecretStore secretStore)
    {
        var fileName = profile == LeafProfile.DnsOnly ? DnsOnlyLeafCertificateFileName : LeafCertificateFileName;
        return GetOrCreateLeaf(
            caCertificatePath: Path.Combine(path1: directory, path2: CaCertificateFileName),
            leafCertificatePath: Path.Combine(path1: directory, path2: fileName),
            secretStore: secretStore,
            profile: profile);
    }

    /// <summary>Overload taking explicit paths and a secret store, for tests. See <see cref="GetOrCreateCa()"/> for behavior.</summary>
    internal static X509Certificate2 GetOrCreateCa(string certificatePath, ProtectedSecretStore secretStore)
    {
        lock (RenewalLock)
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
    }

    /// <summary>
    /// Overload taking explicit paths and a secret store, for tests. See <see cref="GetOrCreateLeaf(string)"/>
    /// for behavior. <paramref name="profile"/> picks the SAN set and the password-secret name; the caller
    /// picks the file, so two profiles must never share <paramref name="leafCertificatePath"/>.
    /// </summary>
    internal static X509Certificate2 GetOrCreateLeaf(string caCertificatePath, string leafCertificatePath,
        ProtectedSecretStore secretStore, LeafProfile profile = LeafProfile.Loopback)
    {
        var passwordSecretName =
            profile == LeafProfile.DnsOnly ? DnsOnlyLeafPasswordSecretName : LeafPasswordSecretName;

        // Reentrant: GetOrCreateCa below acquires the same RenewalLock, and System.Threading.Lock (like
        // the classic `lock` statement it replaces) allows the thread already holding it to re-enter.
        lock (RenewalLock)
        {
            using var ca = GetOrCreateCa(certificatePath: caCertificatePath, secretStore: secretStore);

            var directory = Path.GetDirectoryName(leafCertificatePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(leafCertificatePath) &&
                secretStore.TryRead(name: passwordSecretName, value: out var existingPassword))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path: leafCertificatePath, password: existingPassword,
                    keyStorageFlags: X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.UtcNow.Add(LeafRenewalWindow) && IsIssuedUnder(leaf: existing, ca: ca))
                    return existing;

                existing.Dispose();
            }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(subjectName: "CN=localhost", key: rsa,
                hashAlgorithm: HashAlgorithmName.SHA256, padding: RSASignaturePadding.Pkcs1);

            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName("localhost");
            if (profile == LeafProfile.Loopback)
            {
                // Never on the DnsOnly leaf: see the class remarks on BoringSSL and IP-address constraints.
                subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
                subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
            }

            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                keyUsages: X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(enhancedKeyUsages: [new Oid("1.3.6.1.5.5.7.3.1")],
                    false)); // Server Authentication

            // The AKI names the issuing CA by key, not just by subject. Without it, a client that trusts two
            // CAs with this CA's subject (an old router CA left in the OS store after a reinstall, say) may
            // pick the wrong one: BoringSSL then fails the handshake with CERT_SIGNATURE_FAILURE.
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                certificate: ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));

            var serialNumber = new byte[16];
            RandomNumberGenerator.Fill(serialNumber);

            using var signed = request.Create(
                issuerCertificate: ca,
                notBefore: DateTimeOffset.UtcNow.AddDays(-1),
                notAfter: DateTimeOffset.UtcNow.Add(LeafValidity),
                serialNumber: serialNumber);
            using var leaf = signed.CopyWithPrivateKey(rsa);

            return PersistAndReload(certificate: leaf, certificatePath: leafCertificatePath,
                secretStore: secretStore, passwordSecretName: passwordSecretName);
        }
    }

    /// <summary>
    /// Whether <paramref name="leaf"/>'s Authority Key Identifier names <paramref name="ca"/>'s key, so a
    /// persisted leaf is reused only when it was issued under the CA in use now.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a leaf with no AKI, which is every leaf persisted before leaves
    /// carried one. Such a leaf is re-minted once on its next load, under the same CA, so no client has to
    /// re-trust anything. It also returns <see langword="false"/> for a leaf from an earlier CA (one that
    /// expired and was re-minted, or whose file was deleted), which would otherwise keep being served
    /// until its own renewal window even though no client trusts its issuer any more.
    /// </remarks>
    private static bool IsIssuedUnder(X509Certificate2 leaf, X509Certificate2 ca)
    {
        var authorityKeyId = leaf.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().SingleOrDefault()?.KeyIdentifier;
        var caKeyId = ca.Extensions.OfType<X509SubjectKeyIdentifierExtension>().SingleOrDefault()?.SubjectKeyIdentifierBytes;
        return authorityKeyId is { } leafValue && caKeyId is { } caValue && leafValue.Span.SequenceEqual(caValue.Span);
    }

    /// <summary>
    /// The two leaves the router issues under its one CA, chosen per TLS handshake by
    /// <see cref="SelectLeafProfile"/> (ADR-0013 Amendment 1).
    /// </summary>
    internal enum LeafProfile
    {
        /// <summary>
        /// <c>DNS:localhost</c>, <c>IP:127.0.0.1</c> and <c>IP:::1</c>, in <c>router-leaf.pfx</c> - the
        /// original leaf, kept for handshakes without SNI, which is what an IP-literal URL produces.
        /// </summary>
        Loopback,

        /// <summary>
        /// <c>DNS:localhost</c> only, in <c>router-leaf-dns.pfx</c> - served whenever the client sends
        /// SNI. The only profile BoringSSL-based clients (Bun, so the native Claude Code build) accept,
        /// because it carries no IP-address name for BoringSSL to check against the CA's IP subtrees.
        /// </summary>
        DnsOnly,
    }

    /// <summary>
    /// Exports <paramref name="certificate"/> (private key included) to <paramref name="certificatePath"/>
    /// under a fresh random password stored in <paramref name="secretStore"/>, then reloads and returns it
    /// from the written bytes so the returned instance's key storage flags are consistent regardless of
    /// whether this call created or loaded the certificate.
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
    private static X509Extension BuildNameConstraintsExtension()
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
