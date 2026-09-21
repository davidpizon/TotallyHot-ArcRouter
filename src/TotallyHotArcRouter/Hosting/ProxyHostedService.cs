using Microsoft.AspNetCore.Connections;
using System.Security.Cryptography;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// A hosted service that manages the lifecycle of the proxy server.
/// </summary>
public class ProxyHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly ILogger<ProxyHostedService> _logger;
    private readonly ProxyServer _proxyServer;
    private readonly int _webPort;

    // Guards StopAsync against a start that never bound a listener: when the port was already in use
    // the host still calls StopAsync on every registered service, and stopping a host that never
    // started is pointless work at best.
    private bool _started;

    /// <summary>
    /// Constructs the underlying <see cref="ProxyServer"/> from its dependencies, which are forwarded
    /// verbatim - this type adds hosted-service lifecycle logging and nothing else, so it deliberately
    /// mirrors <see cref="ProxyServer"/>'s own constructor rather than reshaping what it is given.
    /// </summary>
    /// <param name="logger">The logger for this hosted service's own start/stop lifecycle messages.</param>
    /// <param name="proxyLogger">The logger handed to the underlying <see cref="ProxyServer"/>.</param>
    /// <param name="proxyMiddleware">The already-constructed middleware instance used to handle every request.</param>
    /// <param name="hostLifetime">
    /// Used to request an orderly shutdown when the proxy's port is already taken - see
    /// <see cref="StartAsync"/> for why that case stops the host instead of throwing.
    /// </param>
    /// <param name="listenerOptions">The proxy's port and bind-address configuration, forwarded verbatim to <see cref="ProxyServer"/>.</param>
    /// <param name="webInterfaceOptions">The web GUI/gRPC-Web listener's configuration, forwarded verbatim to <see cref="ProxyServer"/>.</param>
    /// <param name="dependencies">
    /// The feature groups hand-carried into <see cref="ProxyServer"/>'s inner DI container; see
    /// <see cref="ProxyServerDependencies"/>. Defaults to <see langword="null"/>, giving a plain
    /// proxy-forwarding server with no admin surfaces beyond the always-mapped ones.
    /// </param>
    public ProxyHostedService(
        ILogger<ProxyHostedService> logger,
        ILogger<ProxyServer> proxyLogger,
        ProxyMiddleware proxyMiddleware,
        IHostApplicationLifetime hostLifetime,
        ProxyListenerOptions? listenerOptions = null,
        WebInterfaceOptions? webInterfaceOptions = null,
        ProxyServerDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(hostLifetime);

        _logger = logger;
        _hostLifetime = hostLifetime;
        _webPort = (webInterfaceOptions ?? new WebInterfaceOptions()).Port;
        _proxyServer = new ProxyServer(logger: proxyLogger, proxyMiddleware: proxyMiddleware,
            listenerOptions: listenerOptions, webInterfaceOptions: webInterfaceOptions, dependencies: dependencies);
    }

    /// <summary>
    /// Starts the proxy server. A port that cannot be bound is reported as a single actionable line and
    /// shuts the host down in an orderly way rather than propagating: the proxy is the reason the process
    /// exists, so there is nothing useful to keep running, and the cause is an operator/environment
    /// condition rather than a defect - Kestrel's own exception carries several frames of stack that tell
    /// an operator nothing they can act on, and letting it escape would tear the process down through the
    /// "terminated unexpectedly" fatal path without stopping the other hosted services first.
    /// </summary>
    /// <remarks>
    /// The message deliberately states the observation and lists the causes rather than naming one. It
    /// previously asserted that another router instance was "most likely" already running, which is only
    /// one possibility and sends an operator looking for a process that may not exist: Windows also hands
    /// back <c>EADDRINUSE</c> for a port reserved by the Host Network Service (Hyper-V, WSL2, container
    /// networking), and those reservations appear in neither <c>netstat</c> nor
    /// <c>netsh interface ipv4 show excludedportrange</c>. A real diagnosis cost several wrong turns on
    /// exactly that, so the remedy - override the port in the machine-shared <c>appsettings.local.json</c>,
    /// which survives MSI upgrades - is named in the message itself.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Proxy Hosted Service is starting.");

        try
        {
            await _proxyServer.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
            WriteDiscoveryFile();
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            // ex.Message already names the address that could not be bound ("Failed to bind to address
            // http://127.0.0.1:5001: address already in use."), and it may be either the proxy port or
            // the gRPC port, so it is quoted rather than reconstructed from the configured values.
            _logger.LogError(
                message:
                "The proxy could not start: {Reason} Either another TotallyHot ArcRouter instance (or the installed Windows service) already holds that port, or the port is reserved by this machine rather than listened on - the Host Network Service used by Hyper-V, WSL2 and container networking reserves whole TCP ranges that show up in neither netstat nor 'netsh interface ipv4 show excludedportrange'. If nothing is listening on the port, set a free one via Proxy:Port in appsettings.local.json in the machine-shared data directory. Shutting down.",
                ex.Message);

            // A failed start must not report success to whatever launched the process; Program's fatal
            // handler sets this for the exceptions it catches, and this path bypasses it by design.
            Environment.ExitCode = 1;
            _hostLifetime.StopApplication();
        }
    }

    /// <summary>
    /// Writes the discovery file (web GUI migration plan Phase P1/P7) a companion process (the Windows
    /// tray, an install script) reads to find the running router's actual web address and the CA
    /// thumbprint it should trust - see <see cref="WebInterfaceDiscoveryFile"/>'s remarks for why this
    /// cannot instead be read back out of <c>appsettings.json</c>. Best-effort: a failure to write it
    /// (a locked-down data directory, say) never affects the caller - a companion process that finds no
    /// discovery file degrades to "no running router found", the same as if the router simply were not
    /// running yet.
    /// </summary>
    private void WriteDiscoveryFile()
    {
        try
        {
            var webAddress = _proxyServer.Addresses.FirstOrDefault(a => new Uri(a).Port == _webPort);
            var caThumbprint = LocalCertificateAuthority.GetOrCreateCa().Thumbprint;

            WebInterfaceDiscoveryFile.Write(new WebInterfaceDiscoveryInfo(WebUrl: webAddress, CaThumbprint: caThumbprint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning(exception: ex,
                message: "Could not write the web interface discovery file; a companion process will not find this router.");
        }
    }

    /// <summary>
    /// Stops the proxy server, unless <see cref="StartAsync"/> never got it listening - see
    /// <c>_started</c> for why that case is a no-op.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return Task.CompletedTask;

        _logger.LogInformation("Proxy Hosted Service is stopping.");
        return _proxyServer.StopAsync(cancellationToken);
    }
}