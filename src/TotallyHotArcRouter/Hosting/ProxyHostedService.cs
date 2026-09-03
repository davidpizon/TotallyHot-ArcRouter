using Microsoft.AspNetCore.Connections;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Hosting
{
    /// <summary>
    /// A hosted service that manages the lifecycle of the proxy server.
    /// </summary>
    public class ProxyHostedService : IHostedService
    {
        private readonly ILogger<ProxyHostedService> _logger;
        private readonly IHostApplicationLifetime _hostLifetime;
        private readonly ProxyServer _proxyServer;

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
        /// <param name="port">The localhost port Kestrel listens on for plain HTTP/1.1 LLM-forwarding traffic.</param>
        /// <param name="grpcPort">The dedicated localhost port for the TLS-secured gRPC endpoint.</param>
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
            int port = 5001,
            int grpcPort = ProxyServer.DefaultGrpcPort,
            ProxyServerDependencies? dependencies = null)
        {
            ArgumentNullException.ThrowIfNull(hostLifetime);

            _logger = logger;
            _hostLifetime = hostLifetime;
            _proxyServer = new ProxyServer(proxyLogger, proxyMiddleware, port, grpcPort, dependencies);
        }

        /// <summary>
        /// Gets the addresses the underlying <see cref="ProxyServer"/> is actually listening on. Only meaningful
        /// after <see cref="StartAsync"/> completes.
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<string> Addresses => _proxyServer.Addresses;

        /// <summary>
        /// Starts the proxy server. A port that is already taken is reported as a single actionable line and
        /// shuts the host down in an orderly way rather than propagating: the proxy is the reason the process
        /// exists, so there is nothing useful to keep running, and the usual cause is an operator condition
        /// (a second instance, or the installed Windows service already holding the port) rather than a defect
        /// - Kestrel's own exception carries several frames of stack that tell an operator nothing they can
        /// act on, and letting it escape would tear the process down through the "terminated unexpectedly"
        /// fatal path without stopping the other hosted services first.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Proxy Hosted Service is starting.");

            try
            {
                await _proxyServer.StartAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
            }
            catch (IOException ex) when (ex.InnerException is AddressInUseException)
            {
                // ex.Message already names the address that could not be bound ("Failed to bind to address
                // http://127.0.0.1:5001: address already in use."), and it may be either the proxy port or
                // the gRPC port, so it is quoted rather than reconstructed from the configured values.
                _logger.LogError(
                    "The proxy could not start: {Reason} Another TotallyHot ArcRouter instance is most likely already running. Shutting down.",
                    ex.Message);

                // A failed start must not report success to whatever launched the process; Program's fatal
                // handler sets this for the exceptions it catches, and this path bypasses it by design.
                Environment.ExitCode = 1;
                _hostLifetime.StopApplication();
            }
        }

        /// <summary>
        /// Stops the proxy server, unless <see cref="StartAsync"/> never got it listening - see
        /// <c>_started</c> for why that case is a no-op.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_started)
            {
                return Task.CompletedTask;
            }

            _logger.LogInformation("Proxy Hosted Service is stopping.");
            return _proxyServer.StopAsync(cancellationToken);
        }
    }
}
