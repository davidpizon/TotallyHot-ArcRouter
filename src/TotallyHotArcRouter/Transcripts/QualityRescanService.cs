using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Background service that grades saved transcript rows, rather than in-flight responses: it sweeps
/// <c>request_transcripts</c> for rows whose <c>scorer_version</c> is missing or stale, re-runs the
/// verifier over the stored prompt and response, and stamps the result back onto the row. A no-op when
/// <see cref="TranscriptOptions.EnableQualityRescan"/> is <see langword="false"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why grade saved data at all when the live path already grades every response?</b> Three things the
/// live trigger structurally cannot do. It cannot <em>backfill</em> - a response dropped because
/// <see cref="QualityOptions.QueueCapacity"/> was full under load is never graded, and load is exactly when
/// the evidence matters most. It cannot <em>re-run</em> - changing a weight or adding a grader only affects
/// traffic from that moment on, so comparing two scorers means waiting weeks rather than re-scoring the
/// corpus you already have. And it cannot <em>throttle</em> - the live queue drops work rather than
/// deferring it, whereas a sweep over saved rows can batch and run off-peak, which is what makes an LLM
/// grader affordable.
/// </para>
/// <para>
/// <b>This deliberately does not write to router memory.</b> <see cref="IQualityScoreObserver"/>'s contract
/// is that <see cref="IQualityScoreAggregator"/> calls it exactly once per request, and
/// <c>RouterMemory</c> accumulates a running sum and count - so a rescan that also observed would add a
/// second observation for every row the live path had already scored, inflating the sample size the voters
/// trust in a way that is invisible in the resulting average. That is precisely the miscount
/// <c>QualityScoreAggregator</c> exists to prevent, and re-introducing it through a second writer would
/// undo that guarantee. What this service produces is a re-measurable scored <em>corpus</em>; deciding
/// which of those scores may reach live memory is a separate change, and belongs with the rework that
/// generalizes the join from one judge to N.
/// </para>
/// <para>
/// Shape follows <see cref="EmbeddingBackfillService"/> - the established pattern for a bounded periodic
/// sweep over saved transcript rows.
/// </para>
/// </remarks>
public sealed class QualityRescanService : BackgroundService
{
    private readonly ILogger<QualityRescanService> _logger;
    private readonly ITranscriptStore _transcriptStore;
    private readonly ISignalExtractor _extractor;
    private readonly IQualityGrader _grader;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly QualityOptions _qualityOptions;

    /// <summary>Initializes a new instance of the <see cref="QualityRescanService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptStore">Supplies rows needing a grade and records the outcome.</param>
    /// <param name="extractor">Mines a gradable snippet out of the saved response text.</param>
    /// <param name="grader">Grades the extracted snippet.</param>
    /// <param name="transcriptOptions">Provides the rescan enable flag, sweep interval, and batch size.</param>
    /// <param name="qualityOptions">Provides <see cref="QualityOptions.ScorerVersion"/> and the verifier enable flag.</param>
    public QualityRescanService(
        ILogger<QualityRescanService> logger,
        ITranscriptStore transcriptStore,
        ISignalExtractor extractor,
        IQualityGrader grader,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<QualityOptions> qualityOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(grader);
        ArgumentNullException.ThrowIfNull(transcriptOptions);
        ArgumentNullException.ThrowIfNull(qualityOptions);

        _logger = logger;
        _transcriptStore = transcriptStore;
        _extractor = extractor;
        _grader = grader;
        _transcriptOptions = transcriptOptions.Value;
        _qualityOptions = qualityOptions.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_transcriptOptions.EnableQualityRescan || !_transcriptOptions.Enabled)
        {
            _logger.LogInformation("Quality rescan is disabled; this loop will not fire.");
            return;
        }

        if (!_qualityOptions.Enabled)
        {
            _logger.LogInformation("Quality verifier is disabled; the quality rescan loop will not fire.");
            return;
        }

        var interval = TimeSpan.FromMinutes(_transcriptOptions.QualityRescanIntervalMinutes);
        _logger.LogInformation(
            "Quality rescan starting; sweeping every {IntervalMinutes} minute(s) in batches of {BatchSize} at scorer version {ScorerVersion}.",
            _transcriptOptions.QualityRescanIntervalMinutes,
            _transcriptOptions.QualityRescanBatchSize,
            _qualityOptions.ScorerVersion);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not end the loop - the next tick retries whatever it did not stamp.
                _logger.LogWarning(ex, "Quality rescan sweep failed; the next sweep will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Grades one bounded batch of rows whose stamp is missing or stale. Internal so tests can drive a
    /// single sweep directly rather than waiting on the <see cref="PeriodicTimer"/>, matching
    /// <see cref="EmbeddingBackfillService.CheckAndBackfillAsync"/>'s convention - including repeating the
    /// enable guard here, so a direct call is a no-op under the same conditions the loop is.
    /// </summary>
    /// <param name="stoppingToken">A cancellation token.</param>
    internal async Task SweepAsync(CancellationToken stoppingToken)
    {
        if (!_transcriptOptions.EnableQualityRescan || !_transcriptOptions.Enabled || !_qualityOptions.Enabled)
        {
            return;
        }

        var ids = await _transcriptStore
            .LoadPendingQualityRescanAsync(_qualityOptions.ScorerVersion, _transcriptOptions.QualityRescanBatchSize, stoppingToken)
            .ConfigureAwait(false);

        if (ids.Count == 0)
        {
            return;
        }

        var graded = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (await RescanOneAsync(id, stoppingToken).ConfigureAwait(false))
            {
                graded++;
            }
            else
            {
                skipped++;
            }
        }

        _logger.LogInformation(
            "Quality rescan swept {Considered} row(s) at scorer version {ScorerVersion}: {Graded} graded, {Skipped} carried no gradable snippet.",
            ids.Count,
            _qualityOptions.ScorerVersion,
            graded,
            skipped);
    }

    /// <summary>
    /// Grades a single saved row and stamps the outcome onto it.
    /// </summary>
    /// <param name="transcriptId">The transcript row id.</param>
    /// <param name="stoppingToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a score was produced; <see langword="false"/> when the row carried nothing gradable.</returns>
    /// <remarks>
    /// A row that yields no snippet is still stamped, with a null score. Leaving it unstamped would put it
    /// back at the head of the very next sweep - and because the sweep is ordered oldest-first and bounded,
    /// a run of prose-only rows would consume every batch forever and no gradable row would ever be
    /// reached.
    /// </remarks>
    private async Task<bool> RescanOneAsync(long transcriptId, CancellationToken stoppingToken)
    {
        var record = await _transcriptStore.GetTranscriptAsync(transcriptId, stoppingToken).ConfigureAwait(false);
        if (record?.ResponseText is not { Length: > 0 } responseText)
        {
            // Selected on `response_text IS NOT NULL`, so this means the row was deleted by retention or
            // emptied between the sweep's select and this read. Nothing to stamp; the row may not exist.
            return false;
        }

        var request = _extractor.Extract(new SignalExtractionContext(
            ResponseText: responseText,
            Prompt: record.PromptText ?? string.Empty,
            Model: record.RoutedModel,
            CorrelationId: record.CorrelationId,
            SessionId: DeriveSessionId(record.CorrelationId)));

        if (request is null)
        {
            await _transcriptStore
                .MarkQualityRescannedAsync(transcriptId, _qualityOptions.ScorerVersion, score: null, stoppingToken)
                .ConfigureAwait(false);
            return false;
        }

        var result = await _grader.GradeAsync(request, stoppingToken).ConfigureAwait(false);
        await _transcriptStore
            .MarkQualityRescannedAsync(transcriptId, _qualityOptions.ScorerVersion, result.UnifiedScore, stoppingToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Recovers the session id from a correlation id.
    /// </summary>
    /// <param name="correlationId">The row's correlation id.</param>
    /// <returns>The session id portion, or <paramref name="correlationId"/> unchanged when it carries no turn suffix.</returns>
    /// <remarks>
    /// <c>request_transcripts</c> stores no session id of its own, but <c>ProxyMiddleware</c> builds every
    /// correlation id as <c>$"{sessionId}:{turnNumber}"</c> at a single site, so the prefix is recoverable
    /// rather than guessed. The split is on the <em>last</em> colon because the turn number cannot contain
    /// one while a session id conceivably could. A correlation id from some other source is passed through
    /// whole, which keeps the value useful for correlation even when it is not strictly a session id.
    /// </remarks>
    private static string DeriveSessionId(string correlationId)
    {
        var separator = correlationId.LastIndexOf(':');
        return separator > 0 ? correlationId[..separator] : correlationId;
    }
}
