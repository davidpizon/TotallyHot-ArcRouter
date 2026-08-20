using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="TaxonomyComparisonService"/>
/// (docs/router/self-organizing-classification-plan.md Phase T4), including that phase's headline exit
/// criterion: fixture traffic engineered so clusters are strictly more predictive than dimensions must
/// produce the expected mean-absolute-error ordering.
/// </summary>
public sealed class TaxonomyComparisonServiceTests : IDisposable
{
    private const string Prefix = "live:";
    private readonly string _tempDirectory;
    private readonly string _dbPath;
    private readonly string _clusterModelPath;

    public TaxonomyComparisonServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "transcripts.db");
        _clusterModelPath = Path.Combine(_tempDirectory, "cluster-model.json");
    }

    [Fact]
    public async Task RunCycle_ClustersStrictlyMorePredictive_ProducesTheExpectedMaeOrdering()
    {
        // Two clusters that split one heuristic dimension in half. Within each cluster the same model
        // scores consistently, but the two clusters disagree - so the dimension-level average sits between
        // them and is wrong for every request, while the cluster-level average is right for each.
        var harness = await BuildHarnessAsync(
            [
                new Sample(Embedding: [1f, 0f], Model: "model-a", Score: 0.9),
                new Sample(Embedding: [1f, 0f], Model: "model-a", Score: 0.9),
                new Sample(Embedding: [0f, 1f], Model: "model-a", Score: 0.1),
                new Sample(Embedding: [0f, 1f], Model: "model-a", Score: 0.1),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, rows.Count);

        var clusterMae = rows.Where(r => r.ClusterAbsoluteError is not null).Average(r => r.ClusterAbsoluteError!.Value);
        var dimensionMae = rows.Where(r => r.DimensionAbsoluteError is not null).Average(r => r.DimensionAbsoluteError!.Value);

        Assert.True(
            clusterMae < dimensionMae,
            $"Expected the learned taxonomy to explain this traffic better, but cluster MAE {clusterMae:F4} was not below dimension MAE {dimensionMae:F4}.");
    }

    [Fact]
    public async Task RunCycle_EveryRowIsLabelledExplorationOrExploitation()
    {
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-a", 0.9, IsExploratory: true),
                new Sample([1f, 0f], "model-a", 0.8, IsExploratory: false),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.IsExploratory);
        Assert.Contains(rows, r => !r.IsExploratory);
    }

    [Fact]
    public async Task RunCycle_PredictionsAreHeldOut_NotScoredAgainstTheirOwnObservation()
    {
        // A single cell holding exactly two observations at 0.2 and 0.8. Scored naively, each row would be
        // compared against the contaminated mean 0.5 (error 0.3). Held out, each is compared against the
        // other observation, so the error is the full 0.6 - the honest number.
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-a", 0.2),
                new Sample([1f, 0f], "model-a", 0.8),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(rows, r => Assert.Equal(0.6, r.ClusterAbsoluteError!.Value, precision: 6));
    }

    [Fact]
    public async Task RunCycle_NoClusterModelTrained_LeavesClusterErrorUnmeasuredRatherThanZero()
    {
        var harness = await BuildHarnessAsync(
            [new Sample([1f, 0f], "model-a", 0.9), new Sample([1f, 0f], "model-a", 0.7)],
            trainClusterModel: false);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Null(r.ClusterAbsoluteError);
            Assert.False(r.IsClustered);
        });
    }

    [Fact]
    public async Task RunCycle_BaselineAbstained_RecordsNoSavingsRatherThanBreakEven()
    {
        // dim_best abstaining means the frozen baseline expressed no preference; a $0 saving would read as
        // "routing broke even", which is a measurement rather than the absence of one.
        var harness = await BuildHarnessAsync(
            [new Sample([1f, 0f], "model-a", 0.9, DimBestModel: null)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(row.EstimatedNetSavingsUsd);
        Assert.Null(row.BaselineEstimatedCostUsd);
    }

    [Fact]
    public async Task RunCycle_CheaperRoutedModel_RecordsAPositiveEstimatedSaving()
    {
        // The router served model-a; dim_best would have served the pricier model-b. Both have observed
        // token averages, so the counterfactual is estimable.
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-b", 0.5, Cost: 0.10m),
                new Sample([1f, 0f], "model-a", 0.9, Cost: 0.01m, DimBestModel: "model-b"),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        var routed = Assert.Single(rows, r => r.RoutedModel == "model-a");
        Assert.Equal("model-b", routed.BaselineModel);
        Assert.NotNull(routed.EstimatedNetSavingsUsd);
        Assert.True(
            routed.EstimatedNetSavingsUsd > 0,
            $"Routing to the cheaper model should record a positive saving, got {routed.EstimatedNetSavingsUsd}.");

        // The converse row is the honest mirror image: serving the pricier model where the baseline's own
        // observed averages were cheaper records a loss, not a floor at zero.
        var lost = Assert.Single(rows, r => r.RoutedModel == "model-b");
        Assert.True(lost.EstimatedNetSavingsUsd < 0);
    }

    [Fact]
    public async Task RunCycle_IsIdempotent_ReprocessingProducesNoDuplicateRows()
    {
        var harness = await BuildHarnessAsync([new Sample([1f, 0f], "model-a", 0.9)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(rows);
    }

    [Fact]
    public async Task RunCycle_TranscriptCaptureDisabled_DoesNothing()
    {
        var harness = await BuildHarnessAsync([new Sample([1f, 0f], "model-a", 0.9)], transcriptsEnabled: false);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunCycle_UnscoredRowsAreNotYetEligible()
    {
        var harness = await BuildHarnessAsync([new Sample([1f, 0f], "model-a", Score: null)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        // A row with no verifier score has nothing for either taxonomy to be measured against.
        Assert.Empty(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunCycle_RecordsRegretFromLedgerPredictionAndRewardWeights()
    {
        // model-b's one prior observation (0.5) is the ledger cell the baseline prediction reads; the
        // routed row scored 0.9 at $0.01 against a much pricier counterfactual, so regret must be negative
        // (dim_best would likely have done worse AND cost more) and exactly the reward difference under
        // the default (ε₁, ε₂) = (1, -0.1) weights.
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-b", 0.5, Cost: 0.10m),
                new Sample([1f, 0f], "model-a", 0.9, Cost: 0.01m, DimBestModel: "model-b"),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        var routed = Assert.Single(rows, r => r.RoutedModel == "model-a");

        Assert.NotNull(routed.BaselinePredictedScore);
        Assert.Equal(0.5, routed.BaselinePredictedScore!.Value, precision: 10);

        Assert.NotNull(routed.EstimatedRegret);
        var expectedRegret =
            ((1.0 * routed.BaselinePredictedScore.Value) + (-0.1 * (double)routed.BaselineEstimatedCostUsd!.Value))
            - ((1.0 * 0.9) + (-0.1 * 0.01));
        Assert.Equal(expectedRegret, routed.EstimatedRegret!.Value, precision: 10);
        Assert.True(routed.EstimatedRegret < 0, $"Routing beat the baseline on both axes; regret should be negative, got {routed.EstimatedRegret}.");
    }

    [Fact]
    public async Task RunCycle_UnpriceableBaseline_RecordsNullRegretOnce()
    {
        // model-c has no price, no observed token averages, and no ledger cell - every regret input is
        // missing, so both estimates stay null. One shot: the comparison row exists, so a second cycle
        // must not requeue or duplicate it.
        var harness = await BuildHarnessAsync(
            [new Sample([1f, 0f], "model-a", 0.9, DimBestModel: "model-c")]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("model-c", row.BaselineModel);
        Assert.Null(row.BaselinePredictedScore);
        Assert.Null(row.EstimatedRegret);
        Assert.Null(row.EstimatedNetSavingsUsd);
    }

    [Fact]
    public async Task RunCycle_RoutedEqualsBaseline_UsesLeaveOneOutPrediction()
    {
        // When the routed model IS the baseline's pick, the observation being compared already sits in the
        // baseline's own ledger cell, so the prediction must hold it out: with observations 0.2 and 0.8 in
        // one cell, each row's baseline prediction is the OTHER observation, never the contaminated 0.5.
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-a", 0.2, DimBestModel: "model-a"),
                new Sample([1f, 0f], "model-a", 0.8, DimBestModel: "model-a"),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        var low = Assert.Single(rows, r => r.ObservedScore == 0.2);
        var high = Assert.Single(rows, r => r.ObservedScore == 0.8);
        Assert.Equal(0.8, low.BaselinePredictedScore!.Value, precision: 10);
        Assert.Equal(0.2, high.BaselinePredictedScore!.Value, precision: 10);
    }

    [Fact]
    public async Task RunCycle_BacklogSpansMultipleBatches_DrainsEveryPendingRow()
    {
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-a", 0.9),
                new Sample([1f, 0f], "model-a", 0.8),
                new Sample([1f, 0f], "model-a", 0.7),
                new Sample([1f, 0f], "model-a", 0.6),
                new Sample([1f, 0f], "model-a", 0.5),
            ],
            batchSize: 2);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        // One cycle drains the whole backlog - three fetches at batch size 2, not one batch per tick.
        var rows = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public async Task RunCycle_RowWithNoDimension_IsNeverQueuedAndTheCycleTerminates()
    {
        // A dimensionless row is not comparable against the frozen taxonomy, and with oldest-first
        // ordering it would sit at the head of every batch: the queue predicate must exclude it, so the
        // cycle both terminates and still compares the row behind it.
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-a", 0.9, Dimension: null),
                new Sample([0f, 1f], "model-a", 0.7),
            ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.7, row.ObservedScore);
    }

    [Fact]
    public async Task RunCycle_RequestsInFlight_DoesNothing()
    {
        var gauge = new InFlightRequestGauge();
        var harness = await BuildHarnessAsync([new Sample([1f, 0f], "model-a", 0.9)], inFlightGauge: gauge);

        using (gauge.Track())
        {
            await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        }

        // The hard-pause guarantee: with a request in flight, the cycle does no comparison work at all.
        Assert.Empty(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunCycle_TrafficArrivesMidDrain_StopsAndResumesNextCycle()
    {
        // The tripping store raises the gauge after the second transcript read, simulating a request
        // arriving mid-drain: the first cycle commits the batch it was in and stops before the next row;
        // the second (idle) cycle finishes the backlog.
        var harness = await BuildHarnessAsync(
            [
                new Sample([1f, 0f], "model-a", 0.9),
                new Sample([1f, 0f], "model-a", 0.8),
                new Sample([1f, 0f], "model-a", 0.7),
                new Sample([1f, 0f], "model-a", 0.6),
            ],
            batchSize: 2,
            wrapTranscriptStore: (gauge, inner) => new GaugeTrippingTranscriptStore(inner, gauge, tripAfterReads: 2));

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var afterFirstCycle = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, afterFirstCycle.Count);

        harness.Gauge!.Decrement();
        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var afterSecondCycle = await harness.ComparisonStore.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, afterSecondCycle.Count);
    }

    /// <summary>One fixture request: its embedding, the model that served it, and what came back.</summary>
    private sealed record Sample(
        float[] Embedding,
        string Model,
        double? Score,
        bool IsExploratory = false,
        decimal? Cost = 0.05m,
        string? DimBestModel = "model-b",
        string? Dimension = "code_generation");

    /// <summary>Everything one test needs to drive a cycle and inspect its output.</summary>
    private sealed record Harness(
        TaxonomyComparisonService Service,
        ITaxonomyComparisonStore ComparisonStore,
        InFlightRequestGauge? Gauge);

    /// <summary>
    /// Builds a fully-wired service over real SQLite stores, seeding one transcript plus one linked memory
    /// entry per sample and (optionally) a two-centroid cluster model over the sample embeddings.
    /// </summary>
    private async Task<Harness> BuildHarnessAsync(
        IReadOnlyList<Sample> samples,
        bool trainClusterModel = true,
        bool transcriptsEnabled = true,
        InFlightRequestGauge? inFlightGauge = null,
        int batchSize = 200,
        Func<InFlightRequestGauge, ITranscriptStore, ITranscriptStore>? wrapTranscriptStore = null)
    {
        var storageOptions = Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = _dbPath,
            ClusterModelPath = _clusterModelPath,
        });
        var transcriptOptions = Options.Create(new TranscriptOptions { Enabled = transcriptsEnabled });

        var database = new TranscriptDatabase(storageOptions);
        database.EnsureCreated();

        var transcriptStore = new SqliteTranscriptStore(database, transcriptOptions);
        var comparisonStore = new SqliteTaxonomyComparisonStore(database, transcriptOptions);
        var memoryEntryStore = new InMemoryEntryStore();
        var routerMemory = new RouterMemory();

        var token = TestContext.Current.CancellationToken;
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var entry = await memoryEntryStore.AppendAsync(
                new MemoryEntry(
                    Id: 0,
                    TaskEmbedding: sample.Embedding,
                    ChosenModel: sample.Model,
                    Score: sample.Score ?? 0,
                    Cost: 0,
                    VerifierTrace: null,
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    IsExploratory: sample.IsExploratory,
                    Propensity: 1.0),
                token);

            var id = await transcriptStore.InsertAsync(
                new TranscriptRecord(
                    Id: 0,
                    CorrelationId: $"session-1:{i}",
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    RequestedModel: "auto",
                    RoutedModel: sample.Model,
                    Dimension: sample.Dimension,
                    Difficulty: "medium",
                    Language: "python",
                    IsUtility: false,
                    PromptText: "write a function",
                    ResponseText: "def f(): ...",
                    Score: null,
                    Cost: sample.Cost,
                    IsExploratory: sample.IsExploratory,
                    Propensity: 1.0,
                    InputTokens: 100,
                    OutputTokens: 50,
                    MemoryEntryId: null,
                    DimBestModel: sample.DimBestModel),
                token);

            if (id is not null)
            {
                await transcriptStore.LinkMemoryEntryAsync(id.Value, entry.Id, token);
                if (sample.Score is { } score)
                {
                    await transcriptStore.UpdateOutcomeAsync($"session-1:{i}", score, token);
                    if (sample.Dimension is { } sampleDimension)
                    {
                        // Mirrors RouterMemoryScoreObserver: live memory is keyed by the live-prefixed dimension.
                        await routerMemory.AddScoreAsync(
                            RouterDimension.ToLiveKey(Prefix, sampleDimension), sample.Model, score);
                    }
                }
            }
        }

        if (trainClusterModel)
        {
            WriteClusterModel();
        }

        inFlightGauge ??= wrapTranscriptStore is null ? null : new InFlightRequestGauge();
        var serviceTranscriptStore = wrapTranscriptStore is null
            ? transcriptStore
            : wrapTranscriptStore(inFlightGauge!, transcriptStore);

        var service = new TaxonomyComparisonService(
            NullLogger<TaxonomyComparisonService>.Instance,
            serviceTranscriptStore,
            comparisonStore,
            memoryEntryStore,
            routerMemory,
            new BenchmarkDatabase(Options.Create(new StorageOptions
            {
                BenchmarkDatabasePath = Path.Combine(_tempDirectory, "no-such-corpus.db"),
            })),
            new StubRouteResolver(),
            transcriptOptions,
            Options.Create(new RoutingOptions { ClusterAssignmentThreshold = 0.5 }),
            storageOptions,
            Options.Create(new SandboxOptions { LiveMemoryPrefix = Prefix }),
            new StubPriceLookup(),
            inFlightGauge,
            batchSize);

        return new Harness(service, comparisonStore, inFlightGauge);
    }

    /// <summary>Writes a two-centroid artifact on the axes the fixture embeddings sit on.</summary>
    private void WriteClusterModel()
    {
        var artifact = new ClusterModelArtifact(
            EmbeddingDimension: 2,
            Centroids: [[1f, 0f], [0f, 1f]],
            ChosenK: 2,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [2, 2],
            ClusterDimensionHistograms: [new Dictionary<string, int>(), new Dictionary<string, int>()],
            ClusterTopTerms: [[], []],
            TrainedFrom: "test",
            BootstrapTaskCount: 0,
            MemoryEntryCount: 4);

        File.WriteAllText(_clusterModelPath, ClusterModelArtifactSerializer.Serialize(artifact));
    }

    /// <summary>
    /// Delegates everything to the real store, but raises <paramref name="gauge"/> after the
    /// <paramref name="tripAfterReads"/>-th transcript read - the seam that lets a test make "a proxy
    /// request arrives" happen at an exact point inside a running drain.
    /// </summary>
    private sealed class GaugeTrippingTranscriptStore(
        ITranscriptStore inner, InFlightRequestGauge gauge, int tripAfterReads) : ITranscriptStore
    {
        private int _reads;

        public async Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            var record = await inner.GetTranscriptAsync(id, cancellationToken);
            if (++_reads == tripAfterReads)
            {
                gauge.Increment();
            }

            return record;
        }

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default) =>
            inner.InsertAsync(record, cancellationToken);

        public Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default) =>
            inner.UpdateOutcomeAsync(correlationId, score, cancellationToken);

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default) =>
            inner.LoadUnembeddedScoredAsync(limit, cancellationToken);

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId, CancellationToken cancellationToken = default) =>
            inner.LinkMemoryEntryAsync(transcriptId, memoryEntryId, cancellationToken);

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) =>
            inner.GetRowCountAsync(cancellationToken);

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default) =>
            inner.DeleteOldestAsync(count, cancellationToken);

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            inner.DeleteBeforeAsync(cutoff, cancellationToken);

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(CancellationToken cancellationToken = default) =>
            inner.LoadPromptTextByMemoryEntryIdAsync(cancellationToken);

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(CancellationToken cancellationToken = default) =>
            inner.LoadObservedTokenAveragesAsync(cancellationToken);
    }

    /// <summary>An in-memory <see cref="IMemoryEntryStore"/>, avoiding a second SQLite file per test.</summary>
    private sealed class InMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            var stored = entry with { Id = _nextId++ };
            _entries.Add(stored);
            return Task.FromResult(stored);
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
    }

    /// <summary>Resolves any model name to a paid route, so the counterfactual reaches the price lookup.</summary>
    private sealed class StubRouteResolver : IModelRouteResolver
    {
        public bool TryResolve(string? modelName, [NotNullWhen(true)] out ResolvedModelRoute? route)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                route = null;
                return false;
            }

            route = new ResolvedModelRoute(
                modelName, "openai", modelName, new Uri("https://example.invalid"), "Authorization", []);
            return true;
        }

        public IReadOnlyList<AvailableModel> ListModels() => [];

        public bool IsProviderEnabled(string provider) => true;

        public bool IsModelEnabled(string modelName) => true;
    }

    /// <summary>Prices model-b well above model-a so a routing saving is unambiguous.</summary>
    private sealed class StubPriceLookup : IModelPriceLookup
    {
        public ModelPrice? TryGetPrice(ModelKey key) => key.ModelName switch
        {
            "model-a" => new ModelPrice(1m, 1m),
            "model-b" => new ModelPrice(100m, 100m),
            _ => null,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }
}
