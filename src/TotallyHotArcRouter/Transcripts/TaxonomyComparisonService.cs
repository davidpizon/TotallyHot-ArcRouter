using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Background service implementing docs/router/self-organizing-classification-plan.md Phase T4's baseline
/// comparison: for every scored, embedded request, it scores the frozen nine-dimension taxonomy and the
/// learned cluster taxonomy against the same observation and records both, alongside an estimated
/// token-cost saving versus what <c>dim_best</c> alone would have chosen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off the request path entirely.</b> The comparison needs a verifier score <em>and</em> a backfilled
/// embedding, neither of which exists when the response is sent, so it drains a queue on a timer instead
/// of running inline - comparison data is deliberately not real-time, and never delays serving a request.
/// </para>
/// <para>
/// <b>Held-out predictions.</b> Both ledgers have already absorbed the observation being scored by the
/// time this runs, so both are queried leave-one-out (<see cref="DimensionLedger.PredictLeaveOneOut"/>,
/// <see cref="ClusterLedger.PredictLeaveOneOut"/>). Scoring either taxonomy against a number it had
/// already folded in would report an optimistic error for both and feed a biased input to the promotion
/// criterion that reads them.
/// </para>
/// <para>
/// <b>Cluster assignment is recomputed, never stored.</b> Cluster ids are meaningless across retrains
/// (Phase T2f's ledger-as-view), so a persisted assignment would silently rot; each cycle assigns against
/// the current artifact instead.
/// </para>
/// </remarks>
public sealed class TaxonomyComparisonService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private const int ComparisonBatchSize = 200;

    private readonly ILogger<TaxonomyComparisonService> _logger;
    private readonly ITranscriptStore _transcriptStore;
    private readonly ITaxonomyComparisonStore _comparisonStore;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly RouterMemory _routerMemory;
    private readonly BenchmarkDatabase _benchmarkDatabase;
    private readonly IModelPriceLookup? _priceLookup;
    private readonly IModelRouteResolver _routeResolver;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly RoutingOptions _routingOptions;
    private readonly string _liveMemoryPrefix;
    private readonly string _clusterModelPath;

    /// <summary>Initializes a new instance of the <see cref="TaxonomyComparisonService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptStore">Supplies the transcript rows being compared and the per-model token averages.</param>
    /// <param name="comparisonStore">The queue of pending rows, and where results are written.</param>
    /// <param name="memoryEntryStore">Supplies the embeddings the learned taxonomy assigns clusters from.</param>
    /// <param name="routerMemory">Backs the frozen taxonomy's live score averages.</param>
    /// <param name="benchmarkDatabase">Backs the frozen taxonomy's offline probing prior.</param>
    /// <param name="priceLookup">Prices the counterfactual, or <see langword="null"/> when no catalog is configured.</param>
    /// <param name="routeResolver">Resolves a baseline model name to the provider its price is keyed by.</param>
    /// <param name="transcriptOptions">Gates the whole loop on transcript capture being enabled.</param>
    /// <param name="routingOptions">Supplies the cluster assignment threshold.</param>
    /// <param name="storageOptions">Supplies the cluster model artifact's path.</param>
    /// <param name="sandboxOptions">Supplies the live-memory key prefix.</param>
    public TaxonomyComparisonService(
        ILogger<TaxonomyComparisonService> logger,
        ITranscriptStore transcriptStore,
        ITaxonomyComparisonStore comparisonStore,
        IMemoryEntryStore memoryEntryStore,
        RouterMemory routerMemory,
        BenchmarkDatabase benchmarkDatabase,
        IModelRouteResolver routeResolver,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<RoutingOptions> routingOptions,
        IOptions<StorageOptions> storageOptions,
        IOptions<SandboxOptions> sandboxOptions,
        IModelPriceLookup? priceLookup = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(comparisonStore);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(routerMemory);
        ArgumentNullException.ThrowIfNull(benchmarkDatabase);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(transcriptOptions);
        ArgumentNullException.ThrowIfNull(routingOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(sandboxOptions);

        _logger = logger;
        _transcriptStore = transcriptStore;
        _comparisonStore = comparisonStore;
        _memoryEntryStore = memoryEntryStore;
        _routerMemory = routerMemory;
        _benchmarkDatabase = benchmarkDatabase;
        _routeResolver = routeResolver;
        _priceLookup = priceLookup;
        _transcriptOptions = transcriptOptions.Value;
        _routingOptions = routingOptions.Value;
        _liveMemoryPrefix = sandboxOptions.Value.LiveMemoryPrefix;
        _clusterModelPath = storageOptions.Value.ResolveClusterModelPath();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_transcriptOptions.Enabled)
        {
            _logger.LogInformation("[TAXONOMY-COMPARE] Transcript capture is disabled; the comparison loop will not fire.");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[TAXONOMY-COMPARE] Comparison cycle threw unexpectedly; continuing.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one comparison cycle - the loop body <see cref="ExecuteAsync"/> runs on every tick. Internal so
    /// tests can drive a single cycle directly instead of waiting on <see cref="CheckInterval"/>, matching
    /// <see cref="EmbeddingBackfillService.CheckAndBackfillAsync"/>'s convention.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!_transcriptOptions.Enabled)
        {
            return;
        }

        var pending = await _comparisonStore.LoadPendingComparisonsAsync(ComparisonBatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        // Everything below is loaded once per cycle, not once per row: the ledgers are whole-corpus
        // aggregates and rebuilding them per transcript would make the cycle quadratic for no gain.
        var entries = await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var entriesById = entries.ToDictionary(e => e.Id);
        var artifact = ClusterModelArtifactLoader.TryLoad(_clusterModelPath, _logger, "taxonomy comparison");
        var clusterLedger = artifact is null
            ? null
            : ClusterLedger.Build(artifact, entries, _routingOptions.ClusterAssignmentThreshold);
        var dimensionLedger = new DimensionLedger(_routerMemory, LoadPriorMatrix(), _liveMemoryPrefix);
        var tokenAverages = await _transcriptStore.LoadObservedTokenAveragesAsync(cancellationToken).ConfigureAwait(false);

        var compared = 0;
        foreach (var transcriptId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transcript = await _transcriptStore.GetTranscriptAsync(transcriptId, cancellationToken).ConfigureAwait(false);
            if (transcript?.Score is not { } observedScore || transcript.Dimension is not { } dimension)
            {
                // The queue selects on score IS NOT NULL, so this is a row that vanished or was captured
                // without a classification - nothing to compare either taxonomy against.
                continue;
            }

            var record = Compare(transcript, observedScore, dimension, entriesById, artifact, clusterLedger, dimensionLedger, tokenAverages);
            await _comparisonStore.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            compared++;

            _logger.LogInformation(
                "[TAXONOMY-COMPARE] Transcript {TranscriptId} ({Model}): observed {Observed:F3}, dimension error {DimensionError}, cluster error {ClusterError}, better {Better}, clustered {IsClustered}, exploratory {IsExploratory}, estimated net savings {Savings} (estimate).",
                record.TranscriptId,
                record.RoutedModel,
                record.ObservedScore,
                record.DimensionAbsoluteError,
                record.ClusterAbsoluteError,
                DescribeWinner(record),
                record.IsClustered,
                record.IsExploratory,
                record.EstimatedNetSavingsUsd);
        }

        if (compared > 0)
        {
            _logger.LogInformation("[TAXONOMY-COMPARE] Cycle complete: compared {Compared} of {Pending} pending rows.", compared, pending.Count);
        }
    }

    /// <summary>Builds one row's comparison from the already-loaded per-cycle ledgers and estimators.</summary>
    /// <param name="transcript">The captured row being compared.</param>
    /// <param name="observedScore">The verifier's score, already known non-null.</param>
    /// <param name="dimension">The row's captured heuristic dimension, already known non-null.</param>
    /// <param name="entriesById">Every current memory entry, indexed by id, for the embedding lookup.</param>
    /// <param name="artifact">The current cluster model, or <see langword="null"/> when none is trained.</param>
    /// <param name="clusterLedger">The ledger built from <paramref name="artifact"/>, or <see langword="null"/> alongside it.</param>
    /// <param name="dimensionLedger">The frozen taxonomy's ledger.</param>
    /// <param name="tokenAverages">Per-model observed token averages backing the counterfactual estimate.</param>
    /// <returns>The comparison to persist.</returns>
    private TaxonomyComparisonRecord Compare(
        TranscriptRecord transcript,
        double observedScore,
        string dimension,
        IReadOnlyDictionary<long, MemoryEntry> entriesById,
        ClusterModelArtifact? artifact,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterLedger.ClusterModelScore>>? clusterLedger,
        DimensionLedger dimensionLedger,
        IReadOnlyDictionary<string, ModelTokenAverage> tokenAverages)
    {
        var liveKey = RouterDimension.ToLiveKey(_liveMemoryPrefix, dimension);
        var dimensionPredicted = dimensionLedger.PredictLeaveOneOut(liveKey, transcript.RoutedModel, observedScore);

        double? clusterPredicted = null;
        var isClustered = false;
        if (artifact is not null
            && clusterLedger is not null
            && transcript.MemoryEntryId is { } memoryEntryId
            && entriesById.TryGetValue(memoryEntryId, out var entry)
            && entry.TaskEmbedding.Length == artifact.EmbeddingDimension)
        {
            var (clusterIndex, similarity) = ClusterLedger.AssignNearestCluster(artifact, entry.TaskEmbedding);
            if (similarity >= _routingOptions.ClusterAssignmentThreshold)
            {
                isClustered = true;
                var key = ModelNameCanonicalizer.Canonicalize(transcript.RoutedModel);
                var cell = clusterLedger.TryGetValue(clusterIndex, out var scores) && scores.TryGetValue(key, out var found)
                    ? found
                    : null;
                clusterPredicted = ClusterLedger.PredictLeaveOneOut(cell, observedScore);
            }
        }

        var (baselineCost, netSavings) = EstimateCounterfactual(transcript, tokenAverages);

        return new TaxonomyComparisonRecord(
            TranscriptId: transcript.Id,
            ComparedAtUtc: DateTimeOffset.UtcNow,
            SessionId: SessionIdOf(transcript.CorrelationId),
            ObservedScore: observedScore,
            DimensionPredictedScore: dimensionPredicted,
            ClusterPredictedScore: clusterPredicted,
            DimensionAbsoluteError: dimensionPredicted is { } d ? Math.Abs(observedScore - d) : null,
            ClusterAbsoluteError: clusterPredicted is { } c ? Math.Abs(observedScore - c) : null,
            IsClustered: isClustered,
            IsExploratory: transcript.IsExploratory,
            RoutedModel: transcript.RoutedModel,
            BaselineModel: transcript.DimBestModel,
            ActualCostUsd: transcript.Cost,
            BaselineEstimatedCostUsd: baselineCost,
            EstimatedNetSavingsUsd: netSavings);
    }

    /// <summary>
    /// Prices what the frozen baseline's pick would have cost, and the resulting net saving against what
    /// the router actually spent.
    /// </summary>
    /// <param name="transcript">The row being compared.</param>
    /// <param name="tokenAverages">Per-model observed token averages.</param>
    /// <returns>The estimated baseline cost and net saving, both <see langword="null"/> when no honest estimate exists.</returns>
    /// <remarks>
    /// Returns nulls rather than zeros whenever any input is missing - an abstaining baseline, an unpriced
    /// model, a model never yet observed, or an unknown actual cost. A zero here would read as "routing
    /// broke even", which is a measurement, not the absence of one.
    /// </remarks>
    private (decimal? BaselineCost, decimal? NetSavings) EstimateCounterfactual(
        TranscriptRecord transcript,
        IReadOnlyDictionary<string, ModelTokenAverage> tokenAverages)
    {
        if (transcript.DimBestModel is not { } baselineModel || transcript.Cost is not { } actualCost)
        {
            return (null, null);
        }

        if (!TryFindAverage(tokenAverages, baselineModel, out var average))
        {
            return (null, null);
        }

        if (!_routeResolver.TryResolve(baselineModel, out var route))
        {
            return (null, null);
        }

        var price = route.IsFree
            ? ModelPrice.Free
            : _priceLookup?.TryGetPrice(new ModelKey(ModelName: route.ModelName, Provider: route.Provider));
        if (price is null)
        {
            return (null, null);
        }

        var baselineCost = price.EstimateCost(
            (int)Math.Round(average.InputTokens), (int)Math.Round(average.OutputTokens));
        return (baselineCost, baselineCost - actualCost);
    }

    /// <summary>
    /// Finds a model's observed token average, matching through
    /// <see cref="ModelNameCanonicalizer.Canonicalize"/> so a counterfactual named in one spelling still
    /// finds rows captured under another.
    /// </summary>
    /// <param name="tokenAverages">The averages keyed by captured model name.</param>
    /// <param name="model">The model to find.</param>
    /// <param name="average">The matching average, when found.</param>
    /// <returns><see langword="true"/> when an average exists for the model.</returns>
    private static bool TryFindAverage(
        IReadOnlyDictionary<string, ModelTokenAverage> tokenAverages,
        string model,
        out ModelTokenAverage average)
    {
        if (tokenAverages.TryGetValue(model, out var exact))
        {
            average = exact;
            return true;
        }

        var canonical = ModelNameCanonicalizer.Canonicalize(model);
        foreach (var (key, value) in tokenAverages)
        {
            if (string.Equals(ModelNameCanonicalizer.Canonicalize(key), canonical, StringComparison.Ordinal))
            {
                average = value;
                return true;
            }
        }

        average = null!;
        return false;
    }

    /// <summary>
    /// Recovers the session id from a correlation id, which <c>ProxyMiddleware</c> composes as
    /// <c>"{sessionId}:{turnNumber}"</c>.
    /// </summary>
    /// <param name="correlationId">The transcript row's correlation id.</param>
    /// <returns>The session portion, or the whole id when it carries no turn suffix.</returns>
    private static string SessionIdOf(string correlationId)
    {
        var lastSeparator = correlationId.LastIndexOf(':');
        return lastSeparator > 0 ? correlationId[..lastSeparator] : correlationId;
    }

    /// <summary>Names which taxonomy predicted this observation better, for the per-row log line.</summary>
    /// <param name="record">The comparison just computed.</param>
    /// <returns>A short label naming the better taxonomy, or that the comparison was not possible.</returns>
    private static string DescribeWinner(TaxonomyComparisonRecord record) =>
        (record.DimensionAbsoluteError, record.ClusterAbsoluteError) switch
        {
            ({ } dimension, { } cluster) when cluster < dimension => "cluster",
            ({ } dimension, { } cluster) when dimension < cluster => "dimension",
            ({ }, { }) => "tie",
            _ => "incomparable",
        };

    /// <summary>
    /// Reads the probing-split prior, returning <see langword="null"/> when the CodeRouterBench corpus is
    /// not synced on this machine - the same degrade <see cref="DimBestVoter"/> already performs.
    /// </summary>
    /// <returns>The probing matrix, or <see langword="null"/> when unavailable.</returns>
    private DimensionModelScoreMatrix? LoadPriorMatrix()
    {
        if (!File.Exists(_benchmarkDatabase.DatabasePath))
        {
            return null;
        }

        try
        {
            return DimensionModelScoreMatrix.FromDatabase(_benchmarkDatabase, "probing");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _logger.LogWarning(ex, "[TAXONOMY-COMPARE] Could not read the CodeRouterBench corpus; comparing against live memory only.");
            return null;
        }
    }
}
