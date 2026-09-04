using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
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
/// <para>
/// <b>Hard pause under load</b> (docs/router/routing-roi-regret-plan.md). When an
/// <see cref="InFlightRequestGauge"/> is supplied, a cycle refuses to start - and a running drain stops
/// before its next row - the moment any proxy request is in flight. Comparison data is analysis, not
/// operation, so it yields the machine entirely rather than merely running at lower priority; a backlog
/// that lags behind sustained traffic is the accepted cost of that guarantee.
/// </para>
/// <para>
/// <b>Drains to completion.</b> Each cycle keeps fetching and comparing batches until the queue is empty
/// (or traffic arrives), instead of the original one-batch-per-tick pacing that let a backlog of N rows
/// take <c>ceil(N/batch)</c> ticks to clear. The queue predicate excludes rows that can never be compared,
/// and a batch that makes no progress breaks the loop, so the drain provably terminates.
/// </para>
/// <para>
/// <b>Heavy inputs are cached across cycles.</b> The memory-entry snapshot, cluster artifact,
/// cluster ledger, and probing prior are rebuilt only when their cheap change stamps (max entry id,
/// artifact/corpus file write times) move, not on every non-empty cycle. Within one cycle everything is
/// loaded once - a row compared late in a long drain scores against the cycle-start snapshot, consistent
/// with the per-cycle semantics this service always had.
/// </para>
/// </remarks>
public sealed class TaxonomyComparisonService : BackgroundService
{
    /// <summary>
    /// The per-fetch batch size a normally-constructed service drains with. Small enough to bound the
    /// window between in-flight checks, large enough that a full drain is a handful of fetches.
    /// </summary>
    private const int DefaultComparisonBatchSize = 200;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private readonly BenchmarkDatabase _benchmarkDatabase;
    private readonly string _clusterModelPath;
    private readonly int _comparisonBatchSize;
    private readonly ITaxonomyComparisonStore _comparisonStore;
    private readonly InFlightRequestGauge? _inFlightGauge;
    private readonly string _liveMemoryPrefix;

    private readonly ILogger<TaxonomyComparisonService> _logger;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly IModelPriceLookup? _priceLookup;
    private readonly IModelRouteResolver _routeResolver;
    private readonly RouterMemory _routerMemory;
    private readonly RoutingOptions _routingOptions;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly ITranscriptStore _transcriptStore;
    private ClusterModelArtifact? _cachedArtifact;
    private DateTime _cachedArtifactStamp;

    private IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterLedger.ClusterModelScore>>?
        _cachedClusterLedger;

    // The cross-cycle cache of the drain's heavy inputs (see the class remarks). Touched only from the
    // single BackgroundService loop (or a test driving RunCycleAsync directly), so no locking is needed.
    private IReadOnlyDictionary<long, MemoryEntry> _cachedEntriesById = new Dictionary<long, MemoryEntry>();
    private long _cachedMaxEntryId = -1;
    private DimensionModelScoreMatrix? _cachedPriorMatrix;
    private DateTime _cachedPriorStamp;
    private bool _priorLoaded;

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
    /// <param name="routingOptions">Supplies the cluster assignment threshold and the reward weights the regret estimate uses.</param>
    /// <param name="storageOptions">Supplies the cluster model artifact's path.</param>
    /// <param name="qualityOptions">Supplies the live-memory key prefix.</param>
    /// <param name="inFlightGauge">
    /// The proxy's in-flight request gauge, or <see langword="null"/> to never pause - the hard-pause
    /// guarantee (see the class remarks) only exists when the gauge does. Defaults to
    /// <see langword="null"/> so existing direct constructions keep their behavior.
    /// </param>
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
        IOptions<QualityOptions> qualityOptions,
        IModelPriceLookup? priceLookup = null,
        InFlightRequestGauge? inFlightGauge = null)
        : this(
            logger: logger, transcriptStore: transcriptStore, comparisonStore: comparisonStore,
            memoryEntryStore: memoryEntryStore, routerMemory: routerMemory, benchmarkDatabase: benchmarkDatabase,
            routeResolver: routeResolver, transcriptOptions: transcriptOptions, routingOptions: routingOptions,
            storageOptions: storageOptions, qualityOptions: qualityOptions, priceLookup: priceLookup,
            inFlightGauge: inFlightGauge, comparisonBatchSize: DefaultComparisonBatchSize)
    {
    }

    /// <summary>
    /// Test-only overload of the public constructor that additionally sets the drain's per-fetch batch
    /// size, so a drain-across-batches test can exercise the multi-batch path with a handful of seeded
    /// rows instead of hundreds (the repo's 5-second unit-test ceiling). Production code always
    /// constructs through the public overload's <see cref="DefaultComparisonBatchSize"/>; the same
    /// internal-for-tests convention as <see cref="RunCycleAsync"/>.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="transcriptStore">Supplies the transcript rows being compared and the per-model token averages.</param>
    /// <param name="comparisonStore">The queue of pending rows, and where results are written.</param>
    /// <param name="memoryEntryStore">Supplies the embeddings the learned taxonomy assigns clusters from.</param>
    /// <param name="routerMemory">Backs the frozen taxonomy's live score averages.</param>
    /// <param name="benchmarkDatabase">Backs the frozen taxonomy's offline probing prior.</param>
    /// <param name="routeResolver">Resolves a baseline model name to the provider its price is keyed by.</param>
    /// <param name="transcriptOptions">Gates the whole loop on transcript capture being enabled.</param>
    /// <param name="routingOptions">Supplies the cluster assignment threshold and reward weights.</param>
    /// <param name="storageOptions">Supplies the cluster model artifact's path.</param>
    /// <param name="qualityOptions">Supplies the live-memory key prefix.</param>
    /// <param name="priceLookup">Prices the counterfactual, or <see langword="null"/> when no catalog is configured.</param>
    /// <param name="inFlightGauge">The proxy's in-flight request gauge, or <see langword="null"/> to never pause.</param>
    /// <param name="comparisonBatchSize">The per-fetch batch size the drain loop uses. Must be positive.</param>
    internal TaxonomyComparisonService(
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
        IOptions<QualityOptions> qualityOptions,
        IModelPriceLookup? priceLookup,
        InFlightRequestGauge? inFlightGauge,
        int comparisonBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(comparisonBatchSize);
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
        ArgumentNullException.ThrowIfNull(qualityOptions);

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
        _liveMemoryPrefix = qualityOptions.Value.LiveMemoryPrefix;
        _clusterModelPath = storageOptions.Value.ResolveClusterModelPath();
        _inFlightGauge = inFlightGauge;
        _comparisonBatchSize = comparisonBatchSize;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_transcriptOptions.Enabled)
        {
            _logger.LogInformation(
                "[TAXONOMY-COMPARE] Transcript capture is disabled; the comparison loop will not fire.");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                try
                {
                    await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(exception: ex,
                        message: "[TAXONOMY-COMPARE] Comparison cycle threw unexpectedly; continuing.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one comparison cycle - the loop body <see cref="ExecuteAsync"/> runs on every tick. A cycle
    /// drains the entire pending queue batch by batch, but only while no proxy request is in flight: it
    /// refuses to start under traffic and abandons the drain before its next row the moment traffic
    /// arrives (rows already compared stay committed; the queue resumes on the next tick). Internal so
    /// tests can drive a single cycle directly instead of waiting on <see cref="CheckInterval"/>, matching
    /// <see cref="EmbeddingBackfillService.CheckAndBackfillAsync"/>'s convention.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!_transcriptOptions.Enabled) return;

        if (IsTrafficInFlight())
        {
            _logger.LogDebug("[TAXONOMY-COMPARE] Skipping this cycle: proxy requests are in flight.");
            return;
        }

        var totalCompared = 0;
        var inputsLoaded = false;
        var entriesById = _cachedEntriesById;
        ClusterModelArtifact? artifact = null;
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterLedger.ClusterModelScore>>? clusterLedger = null;
        DimensionLedger? dimensionLedger = null;
        IReadOnlyDictionary<string, ModelTokenAverage> tokenAverages = new Dictionary<string, ModelTokenAverage>();

        while (true)
        {
            var pending = await _comparisonStore
                .LoadPendingComparisonsAsync(limit: _comparisonBatchSize, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (pending.Count == 0) break;

            if (!inputsLoaded)
            {
                // Loaded once per cycle (on the first non-empty batch, preserving "an empty queue does no
                // work"), not once per batch or per row: the ledgers are whole-corpus aggregates and
                // rebuilding them inside the drain would make it quadratic for no gain. Rows compared late
                // in a long drain score against this cycle-start snapshot - the same per-cycle semantics
                // the one-batch version had.
                (entriesById, artifact, clusterLedger) =
                    await GetClusteringInputsAsync(cancellationToken).ConfigureAwait(false);
                dimensionLedger = new DimensionLedger(routerMemory: _routerMemory, priorMatrix: LoadPriorMatrix(),
                    liveMemoryPrefix: _liveMemoryPrefix);
                tokenAverages = await _transcriptStore.LoadObservedTokenAveragesAsync(cancellationToken)
                    .ConfigureAwait(false);
                inputsLoaded = true;
            }

            var comparedThisBatch = 0;
            foreach (var transcriptId in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsTrafficInFlight())
                {
                    _logger.LogInformation(
                        message:
                        "[TAXONOMY-COMPARE] Pausing the drain after {Compared} row(s): a proxy request arrived; resuming next cycle.",
                        totalCompared + comparedThisBatch);
                    return;
                }

                var transcript = await _transcriptStore
                    .GetTranscriptAsync(id: transcriptId, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (transcript?.Score is not { } observedScore || transcript.Dimension is not { } dimension)
                    // The queue predicate already requires a score and a dimension, so this is a row that
                    // vanished or changed between the id fetch and this read - nothing to compare.
                    continue;

                var record = Compare(transcript: transcript, observedScore: observedScore, dimension: dimension,
                    entriesById: entriesById, artifact: artifact, clusterLedger: clusterLedger,
                    dimensionLedger: dimensionLedger!, tokenAverages: tokenAverages);
                await _comparisonStore.UpsertAsync(record: record, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                comparedThisBatch++;

                _logger.LogInformation(
                    message:
                    "[TAXONOMY-COMPARE] Transcript {TranscriptId} ({Model}): observed {Observed:F3}, dimension error {DimensionError}, cluster error {ClusterError}, better {Better}, clustered {IsClustered}, exploratory {IsExploratory}, estimated net savings {Savings}, estimated regret {Regret} (estimates).",
                    record.TranscriptId,
                    record.RoutedModel,
                    record.ObservedScore,
                    record.DimensionAbsoluteError,
                    record.ClusterAbsoluteError,
                    DescribeWinner(record),
                    record.IsClustered,
                    record.IsExploratory,
                    record.EstimatedNetSavingsUsd,
                    record.EstimatedRegret);
            }

            totalCompared += comparedThisBatch;

            if (comparedThisBatch == 0)
            {
                // The termination proof for the drain: a batch in which nothing was compared cannot make
                // progress on an identical next fetch, so breaking beats spinning. The queue predicate
                // makes this unreachable except for rows deleted (e.g. by retention) between the id fetch
                // and the row read, which the next cycle's fresh fetch no longer returns.
                _logger.LogWarning(
                    message:
                    "[TAXONOMY-COMPARE] A batch of {BatchSize} pending row(s) produced no comparisons; ending the drain to avoid spinning.",
                    pending.Count);
                break;
            }
        }

        if (totalCompared > 0)
            _logger.LogInformation(message: "[TAXONOMY-COMPARE] Cycle complete: drained {Compared} pending row(s).",
                totalCompared);
    }

    /// <summary>
    /// Whether any proxy request is currently being served - the drain's pause signal. Always
    /// <see langword="false"/> when no gauge was supplied, preserving the never-pause behavior of direct
    /// constructions.
    /// </summary>
    private bool IsTrafficInFlight()
    {
        return _inFlightGauge is { Count: > 0 };
    }

    /// <summary>
    /// Returns the memory-entry snapshot, cluster artifact, and cluster ledger for this cycle, rebuilding
    /// the cached copies only when their change stamps moved: the store's max entry id (entries are
    /// append-only, and FIFO eviction only happens alongside an append) and the artifact file's last write
    /// time. The probe is two cheap reads, against a rebuild that loads every embedding and re-aggregates
    /// the whole corpus.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The (possibly cached) entries-by-id snapshot, artifact, and ledger.</returns>
    private async Task<(IReadOnlyDictionary<long, MemoryEntry> EntriesById, ClusterModelArtifact? Artifact,
            IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterLedger.ClusterModelScore>>? ClusterLedger)>
        GetClusteringInputsAsync(CancellationToken cancellationToken)
    {
        var maxEntryId = await _memoryEntryStore.GetMaxIdAsync(cancellationToken).ConfigureAwait(false);
        var artifactStamp = File.Exists(_clusterModelPath)
            ? File.GetLastWriteTimeUtc(_clusterModelPath)
            : DateTime.MinValue;

        if (maxEntryId != _cachedMaxEntryId || artifactStamp != _cachedArtifactStamp)
        {
            var entries = await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            _cachedEntriesById = entries.ToDictionary(e => e.Id);
            _cachedArtifact = ClusterModelArtifactLoader.TryLoad(path: _clusterModelPath, logger: _logger,
                consumer: "taxonomy comparison");
            _cachedClusterLedger = _cachedArtifact is null
                ? null
                : ClusterLedger.Build(artifact: _cachedArtifact, entries: entries,
                    assignmentThreshold: _routingOptions.ClusterAssignmentThreshold);
            _cachedMaxEntryId = maxEntryId;
            _cachedArtifactStamp = artifactStamp;
        }

        return (_cachedEntriesById, _cachedArtifact, _cachedClusterLedger);
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
        var liveKey = RouterDimension.ToLiveKey(liveMemoryPrefix: _liveMemoryPrefix, dimension: dimension);
        var dimensionPredicted = dimensionLedger.PredictLeaveOneOut(dimension: liveKey, model: transcript.RoutedModel,
            observedScore: observedScore);

        double? clusterPredicted = null;
        var isClustered = false;
        if (artifact is not null
            && clusterLedger is not null
            && transcript.MemoryEntryId is { } memoryEntryId
            && entriesById.TryGetValue(key: memoryEntryId, value: out var entry)
            && entry.TaskEmbedding.Length == artifact.EmbeddingDimension)
        {
            var (clusterIndex, similarity) =
                ClusterLedger.AssignNearestCluster(artifact: artifact, embedding: entry.TaskEmbedding);
            if (similarity >= _routingOptions.ClusterAssignmentThreshold)
            {
                isClustered = true;
                var key = ModelNameCanonicalizer.Canonicalize(transcript.RoutedModel);
                var cell = clusterLedger.TryGetValue(key: clusterIndex, value: out var scores) &&
                           scores.TryGetValue(key: key, value: out var found)
                    ? found
                    : null;
                clusterPredicted = ClusterLedger.PredictLeaveOneOut(cell: cell, observedScore: observedScore);
            }
        }

        var (baselineCost, netSavings) = EstimateCounterfactual(transcript: transcript, tokenAverages: tokenAverages);
        var baselinePredicted = PredictBaselineScore(transcript: transcript, liveKey: liveKey,
            observedScore: observedScore, dimensionLedger: dimensionLedger);
        var regret = EstimateRegret(observedScore: observedScore, actualCost: transcript.Cost,
            baselinePredictedScore: baselinePredicted, baselineCost: baselineCost);

        return new TaxonomyComparisonRecord(
            TranscriptId: transcript.Id,
            ComparedAtUtc: DateTimeOffset.UtcNow,
            SessionId: CorrelationIdParser.SessionIdOf(transcript.CorrelationId),
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
            EstimatedNetSavingsUsd: netSavings,
            BaselinePredictedScore: baselinePredicted,
            EstimatedRegret: regret);
    }

    /// <summary>
    /// Predicts the score the <c>dim_best</c> baseline's pick would likely have achieved on this request -
    /// the quality half of the regret estimate, from the same ledger blend <see cref="DimBestVoter"/>
    /// votes on.
    /// </summary>
    /// <param name="transcript">The row being compared.</param>
    /// <param name="liveKey">The row's dimension as a live-memory key.</param>
    /// <param name="observedScore">The verifier's score for the model that actually served the request.</param>
    /// <param name="dimensionLedger">The frozen taxonomy's ledger.</param>
    /// <returns>
    /// The predicted baseline score, or <see langword="null"/> when the baseline abstained or neither ledger source
    /// has the cell.
    /// </returns>
    /// <remarks>
    /// When the routed model <em>is</em> the baseline's pick, the observation being compared has already
    /// been folded into that very cell, so it is queried leave-one-out - the same self-contamination
    /// rationale as the accuracy comparison above. When they differ, the observation lives in a different
    /// model's cell and the plain blend is the honest prediction.
    /// </remarks>
    private double? PredictBaselineScore(
        TranscriptRecord transcript,
        string liveKey,
        double observedScore,
        DimensionLedger dimensionLedger)
    {
        if (transcript.DimBestModel is not { } baselineModel) return null;

        var routedIsBaseline = string.Equals(
            a: ModelNameCanonicalizer.Canonicalize(transcript.RoutedModel),
            b: ModelNameCanonicalizer.Canonicalize(baselineModel),
            comparisonType: StringComparison.Ordinal);

        return routedIsBaseline
            ? dimensionLedger.PredictLeaveOneOut(dimension: liveKey, model: baselineModel, observedScore: observedScore)
            : dimensionLedger.Predict(dimension: liveKey, model: baselineModel);
    }

    /// <summary>
    /// Estimates the routing decision's regret against the <c>dim_best</c> baseline under the canonical
    /// reward <c>r = ε₁·s + ε₂·κ</c> (docs/router/routing-roi-regret-plan.md): the baseline's estimated
    /// reward minus the routed pick's observed reward, using the same
    /// <see cref="RoutingOptions.Epsilon1"/>/<see cref="RoutingOptions.Epsilon2"/> weights
    /// <c>UtilityRoutingPolicy</c> routes with, so the regret figure and the live selection criterion can
    /// never disagree about what "better" means.
    /// </summary>
    /// <param name="observedScore">The routed pick's verifier score.</param>
    /// <param name="actualCost">What the routed pick actually cost, or <see langword="null"/> when unknown.</param>
    /// <param name="baselinePredictedScore">The baseline's predicted score, or <see langword="null"/> when unavailable.</param>
    /// <param name="baselineCost">The baseline's estimated cost, or <see langword="null"/> when unpriceable.</param>
    /// <returns>
    /// The estimated regret - positive when the baseline would likely have earned more reward - or
    /// <see langword="null"/> when any input is missing. Null rather than zero for the same reason as the
    /// savings estimate: a zero reads as "the router broke even", which is a measurement, not the absence
    /// of one.
    /// </returns>
    private double? EstimateRegret(
        double observedScore,
        decimal? actualCost,
        double? baselinePredictedScore,
        decimal? baselineCost)
    {
        if (actualCost is not { } routedCost
            || baselinePredictedScore is not { } baselineScore
            || baselineCost is not { } counterfactualCost)
            return null;

        var routedReward = _routingOptions.Epsilon1 * observedScore + _routingOptions.Epsilon2 * (double)routedCost;
        var baselineReward = _routingOptions.Epsilon1 * baselineScore +
                             _routingOptions.Epsilon2 * (double)counterfactualCost;
        return baselineReward - routedReward;
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
            return (null, null);

        if (!TryFindAverage(tokenAverages: tokenAverages, model: baselineModel, average: out var average))
            return (null, null);

        if (!_routeResolver.TryResolve(modelName: baselineModel, route: out var route)) return (null, null);

        var price = route.IsFree
            ? ModelPrice.Free
            : _priceLookup?.TryGetPrice(new ModelKey(ModelName: route.ModelName, Provider: route.Provider));
        if (price is null) return (null, null);

        var baselineCost = price.EstimateCost(
            promptTokens: (int)Math.Round(average.InputTokens),
            completionTokens: (int)Math.Round(average.OutputTokens));
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
        if (tokenAverages.TryGetValue(key: model, value: out var exact))
        {
            average = exact;
            return true;
        }

        var canonical = ModelNameCanonicalizer.Canonicalize(model);
        foreach (var (key, value) in tokenAverages)
            if (string.Equals(a: ModelNameCanonicalizer.Canonicalize(key), b: canonical,
                    comparisonType: StringComparison.Ordinal))
            {
                average = value;
                return true;
            }

        average = null!;
        return false;
    }

    /// <summary>Names which taxonomy predicted this observation better, for the per-row log line.</summary>
    /// <param name="record">The comparison just computed.</param>
    /// <returns>A short label naming the better taxonomy, or that the comparison was not possible.</returns>
    private static string DescribeWinner(TaxonomyComparisonRecord record)
    {
        return (record.DimensionAbsoluteError, record.ClusterAbsoluteError) switch
        {
            ({ } dimension, { } cluster) when cluster < dimension => "cluster",
            ({ } dimension, { } cluster) when dimension < cluster => "dimension",
            (not null, not null) => "tie",
            _ => "incomparable"
        };
    }

    /// <summary>
    /// Reads the probing-split prior, returning <see langword="null"/> when the CodeRouterBench corpus is
    /// not synced on this machine - the same degrade <see cref="DimBestVoter"/> already performs. Cached
    /// across cycles keyed on the corpus file's last write time: the prior is offline data that only
    /// changes on an explicit benchmark sync, so re-reading the whole split every cycle bought nothing.
    /// </summary>
    /// <returns>The probing matrix, or <see langword="null"/> when unavailable.</returns>
    private DimensionModelScoreMatrix? LoadPriorMatrix()
    {
        var databasePath = _benchmarkDatabase.DatabasePath;
        var stamp = File.Exists(databasePath) ? File.GetLastWriteTimeUtc(databasePath) : DateTime.MinValue;
        if (_priorLoaded && stamp == _cachedPriorStamp) return _cachedPriorMatrix;

        _cachedPriorStamp = stamp;
        _priorLoaded = true;

        if (stamp == DateTime.MinValue)
        {
            _cachedPriorMatrix = null;
            return null;
        }

        try
        {
            _cachedPriorMatrix = DimensionModelScoreMatrix.FromDatabase(database: _benchmarkDatabase, split: "probing");
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(exception: ex,
                message:
                "[TAXONOMY-COMPARE] Could not read the CodeRouterBench corpus; comparing against live memory only.");
            _cachedPriorMatrix = null;
        }

        return _cachedPriorMatrix;
    }
}