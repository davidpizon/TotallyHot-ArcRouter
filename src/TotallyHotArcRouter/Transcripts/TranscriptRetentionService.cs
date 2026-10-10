using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Formerly enforced age and row-count bounds on <c>request_transcripts</c>. #165 phase 2 retires that for
/// production: Sample Size session retention deletes whole sessions (and null-archive transcript rows), and
/// those deletes remove the matching transcript rows. This service stays registered so the host layout does
/// not change, and it no longer deletes. <see cref="ITranscriptStore.DeleteBeforeAsync"/> /
/// <see cref="ITranscriptStore.DeleteOldestAsync"/> remain only for secure-deletion tests.
/// </summary>
public sealed class TranscriptRetentionService : BackgroundService
{
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
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Transcript row retention is retired (capture enabled {Enabled}, max rows {MaxRows}); Sample Size session retention deletes whole sessions.",
            _options.Enabled,
            _options.MaxRows);
        _ = _transcriptStore;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The former purge cycle. It now does nothing: Sample Size session retention is the deleter.
    /// Internal so <c>TranscriptRetentionServiceTests</c> can call it directly.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal Task CheckAndPurgeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}