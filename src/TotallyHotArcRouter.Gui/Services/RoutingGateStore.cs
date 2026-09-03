using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// How the last poll of the router's routing-gate service turned out. Three outcomes, not two, because
/// "the poll failed" and "the router is down" are different facts and the tray tells the user which one it
/// thinks is true: a router that is listening but rejecting calls (a mismatched management token, say) used
/// to be reported to the user as a stopped Windows service, which sent them looking at the service control
/// manager for a problem that was never there.
/// </summary>
public enum RouterConnectionState
{
    /// <summary>The last poll succeeded; <see cref="RoutingGateStore.IsEnabled"/> is meaningful.</summary>
    Connected,

    /// <summary>Nothing is listening - the call failed with gRPC <c>Unavailable</c>. The router really is down.</summary>
    Unreachable,

    /// <summary>
    /// The router answered but refused the call (authentication, permissions, or an internal error). It is
    /// running; something about this client's request is wrong.
    /// </summary>
    Rejected,
}

/// <summary>
/// Singleton view-model backing the tray icon's "Enable Routing"/"Disable Routing" toggle and its
/// service-down detection. Unlike the other admin stores (<see cref="ProviderAdminStore"/>,
/// <see cref="RoutingModeStore"/>, ...), which load once on a component's <c>OnInitializedAsync</c>, this
/// one polls continuously in the background (<see cref="DefaultPollInterval"/>): the tray needs an always-fresh,
/// synchronously readable signal at the moment the user right-clicks the icon, and there is no Blazor
/// component lifecycle to hang a load off of while the dashboard window is hidden - which is almost always
/// (see <c>TrayWindowManager</c>). Registered as a singleton in <c>MauiProgram</c>; <c>TrayWindowManager</c>
/// resolves it via the MAUI service provider once the window is attached.
/// </summary>
public sealed class RoutingGateStore : IAsyncDisposable
{
    /// <summary>The production polling cadence. Overridable via the constructor so tests don't wait out the real 3 seconds.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    private readonly IRoutingGateAdminClient _client;
    private readonly IDisposable? _ownedClient;
    private readonly ILogger<RoutingGateStore>? _logger;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly Task _pollTask;

    private readonly object _stateGate = new();
    private RouterConnectionState _connectionState = RouterConnectionState.Unreachable;
    private string? _lastFailureMessage;
    private bool _isEnabled = true;
    private bool _wasUsable;

    /// <summary>Initializes a new instance of the <see cref="RoutingGateStore"/> class and starts polling.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.</param>
    /// <param name="pollInterval">How often to re-poll the router; defaults to <see cref="DefaultPollInterval"/>. Overridable so a test can assert on the poll loop without waiting out the real cadence.</param>
    public RoutingGateStore(
        ILogger<RoutingGateStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress,
        TimeSpan? pollInterval = null)
    {
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        var client = new RoutingGateAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingGateStore"/> class over a caller-supplied client.
    /// The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    /// <param name="client">The client this store polls and mutates through.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="pollInterval">How often to re-poll the router; defaults to <see cref="DefaultPollInterval"/>. Overridable so a test can assert on the poll loop without waiting out the real cadence.</param>
    public RoutingGateStore(IRoutingGateAdminClient client, ILogger<RoutingGateStore>? logger = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _client = client;
        _ownedClient = null;
        _logger = logger;
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>How the last poll turned out. Drives what the tray tells the user when the menu is unavailable.</summary>
    public RouterConnectionState ConnectionState
    {
        get { lock (_stateGate) { return _connectionState; } }
    }

    /// <summary>
    /// Whether the last poll reached the proxy at all. <see langword="true"/> for
    /// <see cref="RouterConnectionState.Rejected"/> as well as <see cref="RouterConnectionState.Connected"/> -
    /// a router that answers with an error is emphatically reachable. Callers deciding whether the routing
    /// toggle can actually be acted on want <see cref="IsUsable"/>, not this.
    /// </summary>
    public bool IsReachable
    {
        get { lock (_stateGate) { return _connectionState != RouterConnectionState.Unreachable; } }
    }

    /// <summary>Whether the last poll succeeded outright, so <see cref="IsEnabled"/> is meaningful and the routing toggle would do something.</summary>
    public bool IsUsable
    {
        get { lock (_stateGate) { return _connectionState == RouterConnectionState.Connected; } }
    }

    /// <summary>
    /// The failure detail from the last unsuccessful poll, or <see langword="null"/> while
    /// <see cref="ConnectionState"/> is <see cref="RouterConnectionState.Connected"/>. Surfaced so a
    /// <see cref="RouterConnectionState.Rejected"/> router can report <em>why</em> rather than being
    /// flattened into a generic outage message.
    /// </summary>
    public string? LastFailureMessage
    {
        get { lock (_stateGate) { return _lastFailureMessage; } }
    }

    /// <summary>
    /// Whether the proxy currently accepts routing requests, as of the last successful poll. Meaningless
    /// while <see cref="IsUsable"/> is <see langword="false"/>.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_stateGate) { return _isEnabled; } }
    }

    /// <summary>
    /// Raised exactly once when the proxy stops being usable - whether it went away entirely or started
    /// refusing calls - and not again on every poll while it stays that way, so the tray can show a one-time
    /// native balloon rather than repeating it every <see cref="DefaultPollInterval"/>. Raised on whatever
    /// thread the poll loop is running on; the handler should consult <see cref="ConnectionState"/> to say
    /// which of the two happened.
    /// </summary>
    public event Action? BecameUnusable;

    /// <summary>Raised after <see cref="IsReachable"/> or <see cref="IsEnabled"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Enables routing, returning the confirmed post-mutation state.</summary>
    /// <exception cref="RoutingGateAdminException">The call failed or the router is unreachable.</exception>
    public Task<bool> EnableAsync(CancellationToken cancellationToken = default) => SetAsync(true, cancellationToken);

    /// <summary>Disables routing, returning the confirmed post-mutation state.</summary>
    /// <exception cref="RoutingGateAdminException">The call failed or the router is unreachable.</exception>
    public Task<bool> DisableAsync(CancellationToken cancellationToken = default) => SetAsync(false, cancellationToken);

    private async Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken)
    {
        var confirmed = await _client.SetAsync(enabled, cancellationToken).ConfigureAwait(false);
        UpdateState(RouterConnectionState.Connected, failureMessage: null, isEnabled: confirmed);
        return confirmed;
    }

    /// <summary>
    /// Polls <see cref="IRoutingGateAdminClient.GetAsync"/> forever on the configured poll interval, so
    /// <see cref="IsReachable"/>/<see cref="IsEnabled"/> stay fresh without any caller having to trigger a
    /// load - the tray context menu reads them synchronously at click time.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var enabled = await _client.GetAsync(cancellationToken).ConfigureAwait(false);
                UpdateState(RouterConnectionState.Connected, failureMessage: null, isEnabled: enabled);
            }
            catch (RoutingGateAdminException ex)
            {
                // IsUnavailable is the whole point of this branch: only a genuine "nothing is listening"
                // failure means the router is down. Everything else - a rejected token, a permissions
                // error, an internal fault - means it answered, and reporting that as an outage is how
                // the tray ended up telling users their running Windows service had stopped.
                var state = ex.IsUnavailable ? RouterConnectionState.Unreachable : RouterConnectionState.Rejected;
                _logger?.LogWarning(ex, "Failed to poll the routing gate from the router; classified as {State}.", state);
                UpdateState(state, ex.Message, isEnabled: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Updates the cached state, raising <see cref="Changed"/> on any change and
    /// <see cref="BecameUnusable"/> exactly once on a usable-to-unusable transition.
    /// </summary>
    /// <param name="connectionState">How the poll (or mutation) that produced this update turned out.</param>
    /// <param name="failureMessage">The failure detail, or <see langword="null"/> on success.</param>
    /// <param name="isEnabled">The gate's value; only recorded when <paramref name="connectionState"/> is <see cref="RouterConnectionState.Connected"/>.</param>
    private void UpdateState(RouterConnectionState connectionState, string? failureMessage, bool isEnabled)
    {
        bool changed;
        bool becameUnusable;
        var isUsable = connectionState == RouterConnectionState.Connected;
        lock (_stateGate)
        {
            changed = _connectionState != connectionState || (isUsable && _isEnabled != isEnabled);
            becameUnusable = _wasUsable && !isUsable;
            _wasUsable = isUsable;
            _connectionState = connectionState;
            _lastFailureMessage = failureMessage;
            if (isUsable)
            {
                _isEnabled = isEnabled;
            }
        }

        if (becameUnusable)
        {
            BecameUnusable?.Invoke();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _pollCts.Cancel();
        try
        {
            await _pollTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _pollCts.Dispose();
        _ownedClient?.Dispose();
    }
}
