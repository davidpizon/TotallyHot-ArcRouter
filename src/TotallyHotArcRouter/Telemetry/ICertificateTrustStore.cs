using System.Diagnostics;
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
/// <c>LocalSystem</c>), never as a side effect of an unprivileged process.
/// <para>
/// After the <see cref="X509Store"/> write, <see cref="Install"/> re-opens the store and confirms the
/// thumbprint is present. When the store API accepts the add but the certificate is still missing
/// (Windows can drop an unprompted root add under some enterprise policies), it falls back to
/// <c>certutil -f -addstore</c> against the same store name and checks again. Either path that leaves
/// the thumbprint absent throws <see cref="InvalidOperationException"/> so the MSI's
/// <c>InstallCertificate</c> custom action (<c>Return="check"</c>) fails the install instead of
/// finishing with a dashboard that still shows "Not secure".
/// </para>
/// </remarks>
public sealed class WindowsCertificateTrustStore : ICertificateTrustStore
{
    private readonly StoreLocation _location;
    private readonly StoreName _name;
    private readonly Func<string, bool>? _containsThumbprintOverride;

    /// <summary>Initializes a new instance of the <see cref="WindowsCertificateTrustStore"/> class.</summary>
    /// <param name="name">The store to add/remove from; defaults to <see cref="StoreName.Root"/>.</param>
    /// <param name="location">The store location; defaults to <see cref="StoreLocation.LocalMachine"/>.</param>
    public WindowsCertificateTrustStore(StoreName name = StoreName.Root, StoreLocation location = StoreLocation.LocalMachine)
        : this(name, location, containsThumbprintOverride: null)
    {
    }

    /// <summary>
    /// Test constructor: <paramref name="containsThumbprintOverride"/> replaces the post-add store
    /// read so a unit test can force the "thumbprint still missing" path without touching
    /// <see cref="StoreLocation.LocalMachine"/>/<see cref="StoreName.Root"/>.
    /// </summary>
    internal WindowsCertificateTrustStore(StoreName name, StoreLocation location,
        Func<string, bool>? containsThumbprintOverride)
    {
        _name = name;
        _location = location;
        _containsThumbprintOverride = containsThumbprintOverride;
    }

    /// <inheritdoc/>
    public void Install(X509Certificate2 caCertificate)
    {
        ArgumentNullException.ThrowIfNull(caCertificate);

        // Public certificate only - never a private key - before any store write or certutil hand-off.
        using var publicOnly = X509CertificateLoader.LoadCertificate(caCertificate.Export(X509ContentType.Cert));

        AddViaX509Store(publicOnly);
        if (ContainsThumbprint(publicOnly.Thumbprint)) return;

        // X509Store.Add can return without leaving a LocalMachine\Root entry when the add needs an
        // interactive trust confirmation that LocalSystem cannot answer. certutil -f -addstore is the
        // documented, non-interactive path Windows administrators use for the same store.
        AddViaCertutil(publicOnly);
        if (ContainsThumbprint(publicOnly.Thumbprint)) return;

        throw new InvalidOperationException(
            $"Could not install certificate thumbprint {publicOnly.Thumbprint} into {_location}\\{_name}. " +
            "Edge and Chrome will keep showing Not secure for https://localhost until this root is trusted.");
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

    /// <summary>
    /// Replace-in-place write through <see cref="X509Store"/>: remove any existing entry for this
    /// thumbprint, then add the public certificate.
    /// </summary>
    private void AddViaX509Store(X509Certificate2 publicOnly)
    {
        using var store = new X509Store(storeName: _name, storeLocation: _location);
        store.Open(OpenFlags.ReadWrite);
        try
        {
            var existing = store.Certificates.Find(findType: X509FindType.FindByThumbprint,
                findValue: publicOnly.Thumbprint, validOnly: false);
            foreach (var certificate in existing.OfType<X509Certificate2>()) store.Remove(certificate);

            store.Add(publicOnly);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Whether <paramref name="thumbprint"/> is present in this instance's configured store after a
    /// fresh open - the post-condition <see cref="Install"/> requires before returning.
    /// </summary>
    private bool ContainsThumbprint(string thumbprint)
    {
        if (_containsThumbprintOverride is not null) return _containsThumbprintOverride(thumbprint);

        using var store = new X509Store(storeName: _name, storeLocation: _location);
        store.Open(OpenFlags.ReadOnly);
        try
        {
            var found = store.Certificates.Find(findType: X509FindType.FindByThumbprint,
                findValue: thumbprint, validOnly: false);
            return found.Count > 0;
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Writes the public certificate to a temp file and runs <c>certutil -f -addstore</c> against this
    /// instance's store name. Only used when <see cref="AddViaX509Store"/> did not leave the thumbprint
    /// visible - the MSI and elevated CLI both already run as LocalSystem/Administrator.
    /// </summary>
    private void AddViaCertutil(X509Certificate2 publicOnly)
    {
        // CurrentUser stores are for unit tests only; certutil's LocalMachine/CurrentUser switch must
        // match, and tests never need the fallback because X509Store.Add against CurrentUser\My sticks.
        if (_location != StoreLocation.LocalMachine) return;

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"totallyhot-arcrouter-ca-{Guid.NewGuid():N}.cer");
        try
        {
            File.WriteAllBytes(temporaryPath, publicOnly.Export(X509ContentType.Cert));

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "certutil",
                Arguments = $"-f -addstore \"{_name}\" \"{temporaryPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
                throw new InvalidOperationException("Could not start certutil to install the local CA.");

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"certutil -addstore {_name} exited with {process.ExitCode}. stdout: {stdout} stderr: {stderr}");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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
        process.WaitForExit();
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
        process.WaitForExit();
    }
}
