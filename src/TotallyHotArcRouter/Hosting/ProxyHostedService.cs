using TotallyHot.ArcRouter.Proxy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace TotallyHot.ArcRouter.Hosting
{
    /// <summary>
    /// A hosted service that manages the lifecycle of the proxy server.
    /// </summary>
    public class ProxyHostedService : IHostedService
    {
        private readonly ILogger<ProxyHostedService> _logger;
        private readonly ProxyServer _proxyServer;

        /// <summary>
        /// Constructs the underlying <see cref="ProxyServer"/> from its dependencies, which are forwarded
        /// verbatim - this type adds hosted-service lifecycle logging and nothing else, so it deliberately
        /// mirrors <see cref="ProxyServer"/>'s own constructor rather than reshaping what it is given.
        /// </summary>
        /// <param name="logger">The logger for this hosted service's own start/stop lifecycle messages.</param>
        /// <param name="proxyLogger">The logger handed to the underlying <see cref="ProxyServer"/>.</param>
        /// <param name="proxyMiddleware">The already-constructed middleware instance used to handle every request.</param>
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
            int port = 5001,
            int grpcPort = ProxyServer.DefaultGrpcPort,
            ProxyServerDependencies? dependencies = null)
        {
            _logger = logger;
            _proxyServer = new ProxyServer(proxyLogger, proxyMiddleware, port, grpcPort, dependencies);
        }

        /// <summary>
        /// Gets the addresses the underlying <see cref="ProxyServer"/> is actually listening on. Only meaningful
        /// after <see cref="StartAsync"/> completes.
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<string> Addresses => _proxyServer.Addresses;

        /// <summary>
        /// Starts the proxy server.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Proxy Hosted Service is starting.");
            return _proxyServer.StartAsync(cancellationToken);
        }

        /// <summary>
        /// Stops the proxy server.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Proxy Hosted Service is stopping.");
            return _proxyServer.StopAsync(cancellationToken);
        }
    }
}

