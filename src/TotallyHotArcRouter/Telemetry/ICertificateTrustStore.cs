using System.Security.Cryptography.X509Certificates;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Adds or removes the router's local CA (<see cref="LocalCertificateAuthority"/>) from wherever this
/// operating system's TLS clients look for trusted roots - the operation behind the router's
/// <c>--install-certificate</c>/<c>--uninstall-certificate</c> CLI flags (web GUI migration plan Phase
/// P7; ADR-0013). One implementation per OS, since "add this to the trust store" is inherently
/// OS/installer-specific (ADR-0013's own decision drivers) - see <see cref="WindowsCertificateTrustStore"/>,
/// <see cref="LinuxCertificateTrustStore"/>, and <see cref="MacCertificateTrustStore"/>.
/// </summary>
public interface ICertificateTrustStore
{
    /// <summary>Adds <paramref name="caCertificate"/> (public certificate only) as a trusted root, or replaces an existing entry for the same thumbprint.</summary>
    void Install(X509Certificate2 caCertificate);

    /// <summary>Removes any trusted-root entry matching <paramref name="caCertificate"/>'s thumbprint. A no-op if none is present.</summary>
    void Uninstall(X509Certificate2 caCertificate);
}

/// <summary>
/// Installs/uninstalls the local CA in the Windows <c>LocalMachine\Root</c> certificate store via
/// <see cref="X509Store"/> - the same store <c>certmgr.msc</c>'s "Trusted Root Certification Authorities"
/// shows, and what every Windows TLS client (Edge, Chrome, .NET, curl via Schannel) consults. Matches
/// against <see cref="X509Certificate2.Thumbprint"/> rather than subject name, so a stale entry from a
/// previous CA generation (a different key, same subject) is never mistaken for the current one.
/// </summary>
/// <remarks>
/// Defaults to <see cref="StoreName.Root"/>/<see cref="StoreLocation.LocalMachine"/> - the real trust
/// anchor every browser and the .NET/Schannel TLS stack read from - but both are constructor parameters
/// so a test can safely exercise the add/find/remove mechanics against an innocuous store
/// (<see cref="StoreLocation.CurrentUser"/>/<see cref="StoreName.My"/>, say) without ever touching the
/// machine's actual trusted-root list. <see cref="Install"/> requires local Administrator privileges
/// against <see cref="StoreLocation.LocalMachine"/>; the router's own <c>--install-certificate</c> CLI
/// flag is meant to be run elevated (interactively, or as the MSI's deferred custom action running as
/// <c>LocalSystem</c> - Phase P7's still-pending installer wiring), never as a side effect of an
/// unprivileged process.
/// </remarks>
public sealed class WindowsCertificateTrustStore : ICertificateTrustStore
{
    private readonly StoreLocation _location;
    private readonly StoreName _name;

    /// <summary>Initializes a new instance of the <see cref="WindowsCertificateTrustStore"/> class.</summary>
    /// <param name="name">The store to add/remove from; defaults to <see cref="StoreName.Root"/>.</param>
    /// <param name="location">The store location; defaults to <see cref="StoreLocation.LocalMachine"/>.</param>
    public WindowsCertificateTrustStore(StoreName name = StoreName.Root, StoreLocation location = StoreLocation.LocalMachine)
    {
        _name = name;
        _location = location;
    }

    /// <inheritdoc/>
    public void Install(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        using var store = new X509Store(storeName: _name, storeLocation: _location);
        store.Open(OpenFlags.ReadWrite);
        try
        {
            // Replace-in-place: remove any existing entry for this exact thumbprint first, so a repeat
            // --install-certificate run (e.g. after a CA rotation) never leaves two copies behind.
            var existing = store.Certificates.Find(findType: X509FindType.FindByThumbprint,
                findValue: caCertificate.Thumbprint, validOnly: false);
            foreach (var certificate in existing.OfType<X509Certificate2>()) store.Remove(certificate);

            // Only the public certificate belongs in an OS trust store - never a private key, which
            // caCertificate does not need to (and here, does not) carry for this operation.
            store.Add(caCertificate);
        }
        finally
        {
            store.Close();
        }
    }

    /// <inheritdoc/>
    public void Uninstall(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        using var store = new X509Store(storeName: _name, storeLocation: _location);
        store.Open(OpenFlags.ReadWrite);
        try
        {
            var existing = store.Certificates.Find(findType: X509FindType.FindByThumbprint,
                findValue: caCertificate.Thumbprint, validOnly: false);
            foreach (var certificate in existing.OfType<X509Certificate2>()) store.Remove(certificate);
        }
        finally
        {
            store.Close();
        }
    }
}

/// <summary>
/// Installs/uninstalls the local CA via Debian/RPM-family Linux's <c>update-ca-certificates</c>
/// convention: a PEM copy under <c>/usr/local/share/ca-certificates/</c>, refreshed by shelling out to
/// that tool. Trusts the OS-wide store (curl, most system TLS clients), but - as ADR-0013 documents -
/// Chrome-on-Linux and Firefox maintain their own NSS certificate databases separate from this, which is
/// a known, separately-documented gap (spike S5; <c>docs/router/client-tls-setup.md</c>), not something
/// this class can close on its own.
/// </summary>
/// <remarks>
/// Requires root to write under <c>/usr/local/share/ca-certificates/</c> and to run
/// <c>update-ca-certificates</c>; the router's <c>--install-certificate</c>/<c>--uninstall-certificate</c>
/// flags are meant to be run via <c>sudo</c> or as the dedicated service user's install script (Phase P10),
/// never as a side effect of the unprivileged router process itself.
/// </remarks>
public sealed class LinuxCertificateTrustStore : ICertificateTrustStore
{
    private const string CertificateFileName = "totallyhot-arcrouter-ca.crt";
    private const string CertificateDirectory = "/usr/local/share/ca-certificates";

    /// <inheritdoc/>
    public void Install(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        Directory.CreateDirectory(CertificateDirectory);
        File.WriteAllText(path: Path.Combine(CertificateDirectory, CertificateFileName),
            contents: caCertificate.ExportCertificatePem());

        RunUpdateCaCertificates();
    }

    /// <inheritdoc/>
    public void Uninstall(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        var path = Path.Combine(CertificateDirectory, CertificateFileName);
        if (File.Exists(path)) File.Delete(path);

        RunUpdateCaCertificates();
    }

    private static void RunUpdateCaCertificates()
    {
        using var process = System.Diagnostics.Process.Start(fileName: "update-ca-certificates", arguments: "");
        process?.WaitForExit();
    }
}

/// <summary>
/// Installs/uninstalls the local CA in macOS's System keychain via the <c>security</c> command-line
/// tool, trusted for TLS server authentication (<c>trustRoot</c> policy).
/// </summary>
/// <remarks>
/// Requires <c>sudo</c>/an administrator prompt to modify <c>/Library/Keychains/System.keychain</c>; the
/// router's <c>--install-certificate</c>/<c>--uninstall-certificate</c> flags are meant to be run
/// elevated, never as a side effect of the unprivileged router process itself.
/// </remarks>
public sealed class MacCertificateTrustStore : ICertificateTrustStore
{
    private const string SystemKeychainPath = "/Library/Keychains/System.keychain";

    /// <inheritdoc/>
    public void Install(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"totallyhot-arcrouter-ca-{Guid.NewGuid():N}.crt");
        try
        {
            File.WriteAllText(path: temporaryPath, contents: caCertificate.ExportCertificatePem());
            RunSecurity($"add-trusted-cert -d -r trustRoot -k {SystemKeychainPath} {temporaryPath}");
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <inheritdoc/>
    public void Uninstall(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        RunSecurity($"delete-certificate -Z {caCertificate.Thumbprint} {SystemKeychainPath}");
    }

    private static void RunSecurity(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(fileName: "security", arguments: arguments);
        process?.WaitForExit();
    }
}
