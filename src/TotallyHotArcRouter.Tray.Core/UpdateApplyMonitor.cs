using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// Polls the router for the one installer the dashboard asked for, launches it through
/// <see cref="IMsiUpdateApplier"/>, and then stops. The tray shell exits after
/// <see cref="InstallLaunched"/> so the MSI can replace this process's files. This monitor never
/// calls <see cref="Environment.Exit"/> itself, so a unit test can observe the launch without ending
/// the test host (ADR-0028).
/// </summary>
public sealed class UpdateApplyMonitor : IAsyncDisposable
{
    /// <summary>How often a running tray looks for a pending install. Tests pass a shorter interval.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly IMsiUpdateApplier _applier;
    private readonly IUpdateAdminClient _client;
    private readonly ILogger<UpdateApplyMonitor>? _logger;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly TimeSpan _pollInterval;
    private readonly Lock _launchGate = new();
    private readonly Task _pollTask;
    private readonly HttpClient? _ownedHttp;
    private Action? _installLaunched;
    private bool _launched;

    /// <summary>Initializes a new instance of the <see cref="UpdateApplyMonitor"/> class and starts polling.</summary>
    /// <param name="channelProvider">Supplies the shared call invoker this monitor's client is constructed over.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="pollInterval">
    /// How often to ask the router if an install is waiting. Defaults to <see cref="DefaultPollInterval"/>.
    /// </param>
    public UpdateApplyMonitor(
        IRouterChannelProvider channelProvider,
        ILogger<UpdateApplyMonitor>? logger = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(channelProvider);

        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _ownedHttp = new HttpClient();
        _client = new UpdateAdminClient(channelProvider.CallInvoker);
        _applier = new MsiUpdateApplier(httpClient: _ownedHttp, logger: NullLogger<MsiUpdateApplier>.Instance);
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateApplyMonitor"/> class over caller-supplied
    /// dependencies. The seam tests use to claim and apply without a live proxy or a real installer.
    /// </summary>
    /// <param name="client">Claims the pending install.</param>
    /// <param name="applier">Downloads, verifies, and launches the installer.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="pollInterval">How often to poll. Defaults to <see cref="DefaultPollInterval"/>.</param>
    public UpdateApplyMonitor(
        IUpdateAdminClient client,
        IMsiUpdateApplier applier,
        ILogger<UpdateApplyMonitor>? logger = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(applier);

        _client = client;
        _applier = applier;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>
    /// Raised on the poll thread after <see cref="IMsiUpdateApplier.ApplyAsync"/> reports the installer
    /// was launched. A subscriber that arrives after the launch still runs once, because the tray attaches
    /// its handler on the reconnect that created this monitor, which can lose the race with the first poll.
    /// The handler must not dispose this monitor on the same thread; the tray shell posts the exit onto
    /// the UI thread so this loop can finish.
    /// </summary>
    public event Action? InstallLaunched
    {
        add
        {
            bool alreadyLaunched;
            lock (_launchGate)
            {
                _installLaunched += value;
                alreadyLaunched = _launched;
            }

            if (alreadyLaunched) value?.Invoke();
        }
        remove
        {
            lock (_launchGate)
            {
                _installLaunched -= value;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _pollCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pollTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _pollCts.Dispose();
        _ownedHttp?.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var claim = await _client.ClaimPendingApplyAsync(cancellationToken).ConfigureAwait(false);
                if (claim is { Claimed: true, AssetDownloadUrl: { } url, AssetSha256: { } sha })
                {
                    var result = await _applier.ApplyAsync(
                            assetDownloadUrl: url,
                            assetSha256: sha,
                            latestVersion: claim.LatestVersion,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        RaiseInstallLaunched();
                        return;
                    }

                    _logger?.LogWarning(
                        message: "The tray could not launch the update installer: {Message}",
                        result.Message);
                }
            }
            catch (GrpcAdminException ex)
            {
                _logger?.LogDebug(exception: ex, message: "Could not claim a pending update; will retry.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(delay: _pollInterval, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Publishes one launch. A handler added after this still runs, from the thread that subscribed.
    /// </summary>
    private void RaiseInstallLaunched()
    {
        Action? handler;
        lock (_launchGate)
        {
            _launched = true;
            handler = _installLaunched;
        }

        handler?.Invoke();
    }
}
