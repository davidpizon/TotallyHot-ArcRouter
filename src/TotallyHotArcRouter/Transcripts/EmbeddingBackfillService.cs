using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Background service that recovers training samples lost when the embedding model was not warm or the
/// budget expired on the live path (docs/router/self-organizing-classification-plan.md Phase T1d). Runs
/// on a 5-minute check interval, mirroring <see cref="Hosting.LogRegRetrainHostedService"/>'s shape.
/// A no-op when <see cref="TranscriptOptions.EnableEmbeddingBackfill"/> is <see langword="false"/>.
/// </summary>
public sealed class EmbeddingBackfillService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private const int BackfillBatchSize = 100;

    private readonly ILogger<EmbeddingBackfillService> _logger;
    private readonly ITranscriptStore _transcriptStore;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly RoutingOptions _routingOptions;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingBackfillService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptStore">Supplies unembedded scored transcript rows.</param>
    /// <param name="embeddingClient">Computes embeddings for prompt text.</param>
    /// <param name="memoryEntryStore">Persists backfilled memory entries.</param>
    /// <param name="transcriptOptions">Provides the embedding backfill enable flag and other transcript settings.</param>
    /// <param name="routingOptions">Provides the embedding budget timeout.</param>
    public EmbeddingBackfillService(
        ILogger<EmbeddingBackfillService> logger,
        ITranscriptStore transcriptStore,
        IEmbeddingClient embeddingClient,
        IMemoryEntryStore memoryEntryStore,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<RoutingOptions> routingOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(transcriptOptions);
        ArgumentNullException.ThrowIfNull(routingOptions);

        _logger = logger;
        _transcriptStore = transcriptStore;
        _embeddingClient = embeddingClient;
        _memoryEntryStore = memoryEntryStore;
        _transcriptOptions = transcriptOptions.Value;
        _routingOptions = routingOptions.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_transcriptOptions.EnableEmbeddingBackfill || !_transcriptOptions.Enabled)
        {
            _logger.LogInformation("Embedding backfill is disabled; this loop will not fire.");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await CheckAndBackfillAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Embedding backfill check threw unexpectedly; continuing.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one cycle of the backfill check and backfill process - the loop body <see cref="ExecuteAsync"/>
    /// runs on every tick. Internal (not private) so <c>EmbeddingBackfillServiceTests</c> can exercise
    /// one cycle directly rather than waiting on <see cref="CheckInterval"/>, mirroring
    /// <c>Program.ExtractFlag</c>'s "internal for direct test access" convention.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndBackfillAsync(CancellationToken cancellationToken)
    {
        if (!_transcriptOptions.EnableEmbeddingBackfill || !_transcriptOptions.Enabled)
        {
            return;
        }

        // Load up to BackfillBatchSize unembedded scored transcript ids
        var unembeddedIds = await _transcriptStore.LoadUnembeddedScoredAsync(BackfillBatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (unembeddedIds.Count == 0)
        {
            return;
        }

        var successCount = 0;
        var failureCount = 0;

        foreach (var transcriptId in unembeddedIds)
        {
            try
            {
                // Retrieve the full transcript row to get the prompt text
                var transcript = await _transcriptStore.GetTranscriptAsync(transcriptId, cancellationToken)
                    .ConfigureAwait(false);

                if (transcript is null)
                {
                    _logger.LogWarning(
                        "Unembedded transcript row {TranscriptId} not found during backfill; skipping.",
                        transcriptId);
                    continue;
                }

                // Skip if no prompt text to embed
                if (string.IsNullOrWhiteSpace(transcript.PromptText))
                {
                    _logger.LogDebug(
                        "Transcript row {TranscriptId} has no prompt text; skipping embedding backfill.",
                        transcriptId);
                    continue;
                }

                // Embed the prompt text - best effort, log warning and skip on failure
                EmbeddingResult embedding;
                try
                {
                    embedding = await _embeddingClient.EmbedAsync(transcript.PromptText, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to compute embedding for transcript {TranscriptId} during backfill; skipping.",
                        transcriptId);
                    failureCount++;
                    continue;
                }

                // Create the memory entry with the computed embedding
                var memoryEntry = new MemoryEntry(
                    Id: 0,
                    TaskEmbedding: embedding.Vector,
                    ChosenModel: transcript.RoutedModel,
                    Score: transcript.Score ?? 0.0,
                    Cost: (double)(transcript.Cost ?? 0.0m),
                    VerifierTrace: null,
                    CreatedAtUtc: transcript.CreatedAtUtc,
                    IsExploratory: transcript.IsExploratory,
                    Propensity: transcript.Propensity);

                var persistedEntry = await _memoryEntryStore.AppendAsync(memoryEntry, cancellationToken)
                    .ConfigureAwait(false);

                // Link the transcript row back to the memory entry
                await _transcriptStore.LinkMemoryEntryAsync(transcriptId, persistedEntry.Id, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Backfilled embedding for transcript {TranscriptId} (correlation {CorrelationId}); linked to memory entry {MemoryEntryId}.",
                    transcriptId,
                    transcript.CorrelationId,
                    persistedEntry.Id);

                successCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error during embedding backfill for transcript {TranscriptId}; skipping.",
                    transcriptId);
                failureCount++;
            }
        }

        if (successCount > 0 || failureCount > 0)
        {
            _logger.LogInformation(
                "Embedding backfill batch complete: {SuccessCount} succeeded, {FailureCount} failed, {BatchSize} processed.",
                successCount,
                failureCount,
                unembeddedIds.Count);
        }
    }
}
