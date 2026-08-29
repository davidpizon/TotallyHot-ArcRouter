using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Background poller for the Router's self-update check (docs/router/auto-update-plan.md Phase 2).
/// Runs an initial check shortly after startup, then one every <see cref="UpdateOptions.PollInterval"/>,
/// recording each outcome into <see cref="IUpdateStateStore"/>. Mirrors
/// <see cref="TotallyHot.ArcRouter.Transcripts.EmbeddingBackfillService"/>'s <see cref="PeriodicTimer"/>
/// shape.
/// </summary>
/// <remarks>
/// This service only <em>detects</em> an available update - it never applies one. Applying is entirely
/// the GUI's responsibility (downloading and launching the signed MSI installer); this service's role
/// ends at <see cref="UpdateAdminGrpcService.NotifyApplyStarting"/> recording that it is about to happen,
/// per docs/router/packaging-and-distribution.md.
/// </remarks>
public sealed class UpdateCheckHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    private readonly IReleaseCheckClient _releaseCheckClient;
    private readonly IUpdateStateStore _stateStore;
    private readonly UpdateOptions _options;
    private readonly ILogger<UpdateCheckHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateCheckHostedService"/> class.</summary>
    /// <param name="releaseCheckClient">Queries GitHub Releases for a newer version.</param>
    /// <param name="stateStore">Where each check's outcome is recorded for the gRPC admin surface to read.</param>
    /// <param name="options">Auto-update configuration - the enable flag and poll interval.</param>
    /// <param name="logger">The logger.</param>
    public UpdateCheckHostedService(
        IReleaseCheckClient releaseCheckClient,
        IUpdateStateStore stateStore,
        IOptions<UpdateOptions> options,
        ILogger<UpdateCheckHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(releaseCheckClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _releaseCheckClient = releaseCheckClient;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// The delay before the first check, and the tick interval thereafter. Internal so tests can shrink
    /// both without waiting on <see cref="UpdateOptions.PollInterval"/>'s real-world default, mirroring
    /// <see cref="TotallyHot.ArcRouter.Transcripts.EmbeddingBackfillService.CheckAndBackfillAsync"/>'s
    /// "internal for direct test access" convention.
    /// </summary>
    internal TimeSpan InitialDelayOverride { get; init; } = InitialDelay;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Update checking is disabled; the background poller will not run.");
            return;
        }

        try
        {
            await Task.Delay(InitialDelayOverride, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                await RunOneCheckAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one check-and-record cycle. Internal (not private) so tests can exercise a single cycle
    /// directly rather than waiting on the timer, mirroring
    /// <see cref="TotallyHot.ArcRouter.Transcripts.EmbeddingBackfillService.CheckAndBackfillAsync"/>.
    /// </summary>
    internal async Task RunOneCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _releaseCheckClient.CheckAsync(cancellationToken).ConfigureAwait(false);
            _stateStore.Record(result);

            if (result.IsUpdateAvailable)
            {
                _logger.LogInformation(
                    "Update check: a newer Router version is available ({LatestVersion}, current {CurrentVersion}).",
                    result.LatestVersion,
                    result.CurrentVersion);
            }
            else if (result.UnavailableReason != ReleaseCheckUnavailableReason.None)
            {
                _logger.LogInformation(
                    "Update check could not resolve: {Reason} ({Detail})",
                    result.UnavailableReason,
                    result.UnavailableDetail);
            }
            else
            {
                _logger.LogDebug("Update check: Router is up to date at {CurrentVersion}.", result.CurrentVersion);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // IReleaseCheckClient's contract is to never throw, but the poller stays defensive anyway -
            // matching EmbeddingBackfillService's ExecuteAsync loop - so an unexpected implementation bug
            // degrades to "this tick logged an error" rather than killing the background service.
            _logger.LogError(ex, "Update check threw unexpectedly; continuing.");
        }
    }
}
