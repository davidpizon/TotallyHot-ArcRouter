using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Background service that enforces configurable age and size bounds on the transcript table
/// (docs/router/self-organizing-classification-plan.md Phase T1e). Runs on a 5-minute check interval,
/// mirroring <see cref="Hosting.LogRegRetrainHostedService"/>'s shape. A no-op when
/// <see cref="TranscriptOptions.Enabled"/> is <see langword="false"/>.
/// </summary>
public sealed class TranscriptRetentionService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<TranscriptRetentionService> _logger;
    private readonly TranscriptOptions _options;
    private readonly ITranscriptStore _transcriptStore;

    /// <summary>Initializes a new instance of the <see cref="TranscriptRetentionService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptStore">Supplies row count and deletion operations.</param>
    /// <param name="options">Provides retention configuration (days and max rows).</param>
    public TranscriptRetentionService(
        ILogger<TranscriptRetentionService> logger,
        ITranscriptStore transcriptStore,
        IOptions<TranscriptOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _transcriptStore = transcriptStore;
        _options = options.Value;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Transcript capture is disabled; retention loop will not fire.");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                try
                {
                    await CheckAndPurgeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(exception: ex,
                        message: "Transcript retention check threw unexpectedly; continuing.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one cycle of the retention purge - the loop body <see cref="ExecuteAsync"/>
    /// runs on every tick. Internal (not private) so <c>TranscriptRetentionServiceTests</c> can exercise
    /// one cycle directly rather than waiting on <see cref="CheckInterval"/>, mirroring
    /// <c>Program.ExtractFlag</c>'s "internal for direct test access" convention.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndPurgeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;

        var rowCount = await _transcriptStore.GetRowCountAsync(cancellationToken).ConfigureAwait(false);
        var deletedByOverage = 0;
        int deletedByAge;

        // First, enforce the max-rows bound by deleting oldest-first if over the limit
        if (rowCount > _options.MaxRows)
        {
            var overageCount = rowCount - _options.MaxRows;
            deletedByOverage = await _transcriptStore
                .DeleteOldestAsync(count: overageCount, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        // Then, delete rows past the retention age
        var cutoffTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
        deletedByAge = await _transcriptStore
            .DeleteBeforeAsync(cutoff: cutoffTime, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (deletedByOverage > 0 || deletedByAge > 0)
            _logger.LogInformation(
                message:
                "Transcript retention purge complete: {DeletedByOverage} rows deleted by overage, {DeletedByAge} rows deleted by age.",
                deletedByOverage,
                deletedByAge);
    }
}