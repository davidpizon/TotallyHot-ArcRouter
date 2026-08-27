using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

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
    private bool _isReachable;
    private bool _isEnabled = true;
    private bool _wasReachable;

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

    /// <summary>Whether the last poll reached the proxy.</summary>
    public bool IsReachable
    {
        get { lock (_stateGate) { return _isReachable; } }
    }

    /// <summary>
    /// Whether the proxy currently accepts routing requests, as of the last successful poll. Meaningless
    /// while <see cref="IsReachable"/> is <see langword="false"/>.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_stateGate) { return _isEnabled; } }
    }

    /// <summary>
    /// Raised exactly once when the proxy transitions from reachable to unreachable (not on every poll while
    /// it stays down), so the tray can show a one-time native balloon rather than repeating it every
    /// <see cref="DefaultPollInterval"/>. Raised on whatever thread the poll loop is running on.
    /// </summary>
    public event Action? BecameUnreachable;

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
        UpdateState(isReachable: true, isEnabled: confirmed);
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
                UpdateState(isReachable: true, isEnabled: enabled);
            }
            catch (RoutingGateAdminException ex)
            {
                _logger?.LogWarning(ex, "Failed to poll the routing gate from the router.");
                UpdateState(isReachable: false, isEnabled: false);
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
    /// <see cref="BecameUnreachable"/> exactly once on a reachable-to-unreachable transition.
    /// </summary>
    private void UpdateState(bool isReachable, bool isEnabled)
    {
        bool changed;
        bool becameUnreachable;
        lock (_stateGate)
        {
            changed = _isReachable != isReachable || (isReachable && _isEnabled != isEnabled);
            becameUnreachable = _wasReachable && !isReachable;
            _wasReachable = isReachable;
            _isReachable = isReachable;
            if (isReachable)
            {
                _isEnabled = isEnabled;
            }
        }

        if (becameUnreachable)
        {
            BecameUnreachable?.Invoke();
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
