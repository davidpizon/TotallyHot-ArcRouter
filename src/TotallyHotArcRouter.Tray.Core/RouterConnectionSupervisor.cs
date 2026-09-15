using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// Keeps the tray connected to the router across everything a long-lived tray process outlives: the router
/// not being up yet, a temporary network blip, and - the case that makes this class necessary rather than
/// a one-shot connect - a router restart, which invalidates every outstanding ADR-0012 session ticket
/// (the ticket store is in-memory; see <c>TelemetryChannelFactory.CreateSessionAuthenticatedAsync</c>'s
/// remarks). Unlike the retired <c>x-admin-token</c> file-based scheme, a session cookie does not survive
/// that restart on its own, so something has to notice and re-authenticate - this is that something.
/// </summary>
/// <remarks>
/// Runs one background loop for the whole tray process lifetime: whenever there is no current, usable
/// <see cref="RoutingGateMonitor"/> (none yet, or its <see cref="RoutingGateMonitor.IsUsable"/> has gone
/// false), attempts a fresh <see cref="ISessionRouterConnector.ConnectAsync"/> and, on success, swaps in a
/// new monitor over the new connection - disposing the old one only after the new one is in place, so
/// <see cref="Monitor"/> is never observed to regress from "something" to "nothing". A connect failure
/// (the router still isn't up, say) is silently retried next tick; there is no bounded retry count because
/// there is no terminal failure state for a tray icon that is supposed to just sit there until the router
/// comes back.
/// </remarks>
public sealed class RouterConnectionSupervisor : IAsyncDisposable
{
    /// <summary>How often to check whether a (re)connect is needed. Overridable so tests don't wait out the real cadence.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(10);

    private readonly ISessionRouterConnector _connector;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<RouterConnectionSupervisor>? _logger;
    private readonly Task _loopTask;
    private readonly Func<IRouterChannelProvider, RoutingGateMonitor> _monitorFactory;
    private readonly TimeSpan _retryInterval;
    private readonly string _serverAddress;

    private IRouterChannelProvider? _currentProvider;

    /// <summary>Initializes a new instance of the <see cref="RouterConnectionSupervisor"/> class and starts its background loop.</summary>
    /// <param name="connector">Establishes each connection attempt.</param>
    /// <param name="serverAddress">The router's TLS endpoint to connect to.</param>
    /// <param name="retryInterval">
    /// How often to check whether a (re)connect is needed; defaults to <see cref="DefaultRetryInterval"/>.
    /// Overridable so a test can assert on the loop without waiting out the real cadence.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="monitorFactory">
    /// Builds the <see cref="RoutingGateMonitor"/> for a freshly connected provider; defaults to
    /// <see cref="RoutingGateMonitor"/>'s own production constructor. The seam tests use to substitute a
    /// monitor built over a fake <c>IRoutingGateAdminClient</c> instead of a real gRPC call invoker, since
    /// a fake <see cref="IRouterChannelProvider.CallInvoker"/> alone can't stand in for a live channel.
    /// </param>
    public RouterConnectionSupervisor(ISessionRouterConnector connector, string serverAddress,
        TimeSpan? retryInterval = null, ILogger<RouterConnectionSupervisor>? logger = null,
        Func<IRouterChannelProvider, RoutingGateMonitor>? monitorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(serverAddress);

        _connector = connector;
        _serverAddress = serverAddress;
        _retryInterval = retryInterval ?? DefaultRetryInterval;
        _logger = logger;
        _monitorFactory = monitorFactory ?? (provider => new RoutingGateMonitor(provider));
        _loopTask = SuperviseAsync(_cts.Token);
    }

    /// <summary>
    /// The current routing-gate monitor, or <see langword="null"/> before the first successful connection.
    /// A caller should treat this as a live handle, not a snapshot - a reconnect can replace it at any
    /// time, so re-read this property rather than holding a reference across an await.
    /// </summary>
    public RoutingGateMonitor? Monitor { get; private set; }

    /// <summary>
    /// The channel provider backing <see cref="Monitor"/>, or <see langword="null"/> before the first
    /// successful connection - so a caller can build another admin client (e.g. an update check) over the
    /// exact same authenticated channel rather than opening a second one. Swapped in lockstep with
    /// <see cref="Monitor"/>; re-read it after every use rather than holding a reference across an await,
    /// for the same reconnect-can-replace-it-at-any-time reason as <see cref="Monitor"/>.
    /// </summary>
    public IRouterChannelProvider? Provider => _currentProvider;

    /// <summary>
    /// Raised after <see cref="Monitor"/> is replaced by a fresh connection - the tray shell re-subscribes
    /// its <see cref="RoutingGateMonitor.BecameUnusable"/> handler to the new instance from here, since the
    /// old instance's own subscription died with it.
    /// </summary>
    public event Action? Reconnected;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();

        if (Monitor is not null) await Monitor.DisposeAsync().ConfigureAwait(false);
        (_currentProvider as IDisposable)?.Dispose();
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Monitor is null || !Monitor.IsUsable) await TryReconnectAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(delay: _retryInterval, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        IRouterChannelProvider provider;
        try
        {
            provider = await _connector.ConnectAsync(serverAddress: _serverAddress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException
            && !cancellationToken.IsCancellationRequested)
        {
            // A connect failure here just means the router isn't up yet (or dropped mid-restart) - the
            // supervisor's own loop retries every _retryInterval, so this is swallowed rather than logged
            // as an error. TaskCanceledException is included because HttpClient throws it for its own
            // request timeout, distinct from this method's cancellationToken (guarded by the condition
            // above so a genuine caller-cancellation still propagates instead of being swallowed).
            // UriFormatException covers a malformed serverAddress (a stale/corrupt discovery-file write
            // mid-crash) - degrades to "keeps retrying, never connects" rather than an unobserved
            // background-task exception, matching every other unreachable-router case this loop already
            // treats as retry-forever rather than fatal.
            _logger?.LogDebug(exception: ex, message: "Could not (re)connect to the router; will retry.");
            return;
        }

        var newMonitor = _monitorFactory(provider);

        var oldMonitor = Monitor;
        var oldProvider = _currentProvider;
        Monitor = newMonitor;
        _currentProvider = provider;

        if (oldMonitor is not null) await oldMonitor.DisposeAsync().ConfigureAwait(false);
        (oldProvider as IDisposable)?.Dispose();

        Reconnected?.Invoke();
    }
}
