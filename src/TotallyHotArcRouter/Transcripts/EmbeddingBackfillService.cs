using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Background service that recovers training samples lost when the embedding model was not warm or the
/// budget expired on the live path (docs/router/self-organizing-classification-plan.md Phase T1d). Runs
/// on a 5-minute check interval, mirroring <see cref="Hosting.LogRegRetrainHostedService"/>'s shape.
/// A no-op when <see cref="TranscriptOptions.EnableEmbeddingBackfill"/> is <see langword="false"/>.
/// <para>
/// Deliberately applies no <see cref="RoutingOptions.EmbeddingBudgetMs"/> to its embedding calls,
/// unlike <c>RequestInterceptor.TryComputeEmbeddingAsync</c> on the live path. That budget exists so the
/// routing hot path never blocks on learning; this loop is the recovery for samples that budget already
/// dropped, and cutting it off on the same deadline would leave them permanently unembedded.
/// </para>
/// </summary>
public sealed class EmbeddingBackfillService : BackgroundService
{
    private const int BackfillBatchSize = 100;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private readonly IEmbeddingClient _embeddingClient;

    private readonly ILogger<EmbeddingBackfillService> _logger;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly ITranscriptStore _transcriptStore;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingBackfillService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptStore">Supplies unembedded scored transcript rows.</param>
    /// <param name="embeddingClient">Computes embeddings for prompt text.</param>
    /// <param name="memoryEntryStore">Persists backfilled memory entries.</param>
    /// <param name="transcriptOptions">Provides the embedding backfill enable flag and other transcript settings.</param>
    public EmbeddingBackfillService(
        ILogger<EmbeddingBackfillService> logger,
        ITranscriptStore transcriptStore,
        IEmbeddingClient embeddingClient,
        IMemoryEntryStore memoryEntryStore,
        IOptions<TranscriptOptions> transcriptOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(transcriptOptions);

        _logger = logger;
        _transcriptStore = transcriptStore;
        _embeddingClient = embeddingClient;
        _memoryEntryStore = memoryEntryStore;
        _transcriptOptions = transcriptOptions.Value;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_transcriptOptions.EnableEmbeddingBackfill || !_transcriptOptions.Enabled)
        {
            _logger.LogInformation("Embedding backfill is disabled; this loop will not fire.");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                try
                {
                    await CheckAndBackfillAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(exception: ex,
                        message: "Embedding backfill check threw unexpectedly; continuing.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
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
        if (!_transcriptOptions.EnableEmbeddingBackfill || !_transcriptOptions.Enabled) return;

        // Load up to BackfillBatchSize unembedded scored transcript ids
        var unembeddedIds = await _transcriptStore
            .LoadUnembeddedScoredAsync(limit: BackfillBatchSize, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (unembeddedIds.Count == 0) return;

        var successCount = 0;
        var failureCount = 0;

        foreach (var transcriptId in unembeddedIds)
            try
            {
                // Retrieve the full transcript row to get the prompt text
                var transcript = await _transcriptStore
                    .GetTranscriptAsync(id: transcriptId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (transcript is null)
                {
                    _logger.LogWarning(
                        message: "Unembedded transcript row {TranscriptId} not found during backfill; skipping.",
                        transcriptId);
                    continue;
                }

                // Skip if no prompt text to embed
                if (string.IsNullOrWhiteSpace(transcript.PromptText))
                {
                    _logger.LogDebug(
                        message: "Transcript row {TranscriptId} has no prompt text; skipping embedding backfill.",
                        transcriptId);
                    continue;
                }

                // Embed the prompt text - best effort, log warning and skip on failure
                EmbeddingResult embedding;
                try
                {
                    embedding = await _embeddingClient
                        .EmbedAsync(text: transcript.PromptText, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception: ex,
                        message: "Failed to compute embedding for transcript {TranscriptId} during backfill; skipping.",
                        transcriptId);
                    failureCount++;
                    continue;
                }

                // Create the memory entry with the computed embedding
                var memoryEntry = new MemoryEntry(
                    0,
                    TaskEmbedding: embedding.Vector,
                    ChosenModel: transcript.RoutedModel,
                    Score: transcript.Score ?? 0.0,
                    Cost: (double)(transcript.Cost ?? 0.0m),
                    null,
                    CreatedAtUtc: transcript.CreatedAtUtc,
                    IsExploratory: transcript.IsExploratory,
                    Propensity: transcript.Propensity,
                    // Stamped from the client that just produced this vector, exactly as
                    // EmbeddingMemory.AddEntryAsync does for the request-path writes. Backfilled entries
                    // are computed here and now, so they carry the current identity - not whatever model
                    // was configured when the underlying transcript row was originally captured.
                    EmbeddingModel: _embeddingClient.ModelIdentity);

                var persistedEntry = await _memoryEntryStore
                    .AppendAsync(entry: memoryEntry, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // Link the transcript row back to the memory entry
                await _transcriptStore.LinkMemoryEntryAsync(transcriptId: transcriptId,
                        memoryEntryId: persistedEntry.Id, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    message:
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
                    exception: ex,
                    message: "Unexpected error during embedding backfill for transcript {TranscriptId}; skipping.",
                    transcriptId);
                failureCount++;
            }

        if (successCount > 0 || failureCount > 0)
            _logger.LogInformation(
                message:
                "Embedding backfill batch complete: {SuccessCount} succeeded, {FailureCount} failed, {BatchSize} processed.",
                successCount,
                failureCount,
                unembeddedIds.Count);
    }
}