using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

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
    private readonly string _clusterModelPath;
    private readonly string _dbPath;
    private readonly string _tempDirectory;

    public TaxonomyComparisonServiceTests()
    {
        _tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(path1: _tempDirectory, path2: "transcripts.db");
        _clusterModelPath = Path.Combine(path1: _tempDirectory, path2: "cluster-model.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(path: _tempDirectory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }

    [Fact]
    public async Task RunCycle_ClustersStrictlyMorePredictive_ProducesTheExpectedMaeOrdering()
    {
        // Two clusters that split one heuristic dimension in half. Within each cluster the same model
        // scores consistently, but the two clusters disagree - so the dimension-level average sits between
        // them and is wrong for every request, while the cluster-level average is right for each.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9),
            new Sample(Embedding: [0f, 1f], Model: "model-a", 0.1),
            new Sample(Embedding: [0f, 1f], Model: "model-a", 0.1)
        ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, actual: rows.Count);

        var clusterMae = rows.Where(r => r.ClusterAbsoluteError is not null)
            .Average(r => r.ClusterAbsoluteError!.Value);
        var dimensionMae = rows.Where(r => r.DimensionAbsoluteError is not null)
            .Average(r => r.DimensionAbsoluteError!.Value);

        Assert.True(
            condition: clusterMae < dimensionMae,
            userMessage:
            $"Expected the learned taxonomy to explain this traffic better, but cluster MAE {clusterMae:F4} was not below dimension MAE {dimensionMae:F4}.");
    }

    [Fact]
    public async Task RunCycle_EveryRowIsLabelledExplorationOrExploitation()
    {
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, true),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.8)
        ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, actual: rows.Count);
        Assert.Contains(collection: rows, filter: r => r.IsExploratory);
        Assert.Contains(collection: rows, filter: r => !r.IsExploratory);
    }

    [Fact]
    public async Task RunCycle_PredictionsAreHeldOut_NotScoredAgainstTheirOwnObservation()
    {
        // A single cell holding exactly two observations at 0.2 and 0.8. Scored naively, each row would be
        // compared against the contaminated mean 0.5 (error 0.3). Held out, each is compared against the
        // other observation, so the error is the full 0.6 - the honest number.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.2),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.8)
        ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(collection: rows, action: r => Assert.Equal(0.6, actual: r.ClusterAbsoluteError!.Value, 6));
    }

    [Fact]
    public async Task RunCycle_NoClusterModelTrained_LeavesClusterErrorUnmeasuredRatherThanZero()
    {
        var harness = await BuildHarnessAsync(
            samples:
            [
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.7)
            ],
            false);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(rows);
        Assert.All(collection: rows, action: r =>
        {
            Assert.Null(r.ClusterAbsoluteError);
            Assert.False(r.IsClustered);
        });
    }

    [Fact]
    public async Task RunCycle_BaselineAbstained_RecordsNoSavingsRatherThanBreakEven()
    {
        // The untrained baseline abstaining means it expressed no preference; a $0 saving would read as
        // "routing broke even", which is a measurement rather than the absence of one.
        var harness = await BuildHarnessAsync(
            [new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, UntrainedBaselineModel: null)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(row.EstimatedNetSavingsUsd);
        Assert.Null(row.BaselineEstimatedCostUsd);
    }

    [Fact]
    public async Task RunCycle_PricesLargeAndSmallTurnsDifferently()
    {
        // ADR-0009's headline regression, stated the way the ADR states it: two turns with the same
        // baseline model, differing only in how much input they actually consumed. Before this change both
        // produced the *identical* baseline cost, because the estimate came from one all-time per-model
        // average - which is exactly what made the per-turn ROI bars uninformative.
        var small = await BaselineCostForTurnAsync(inputTokens: 500, tokenCounter: new TokenCounterRegistry());
        var large = await BaselineCostForTurnAsync(inputTokens: 150_000, tokenCounter: new TokenCounterRegistry());

        // model-b is priced at $100/MTok both ways and averages 50 output tokens in this fixture, so the
        // exact figures are pinned: the input half now tracks the turn while the output half does not.
        Assert.Equal(expected: 0.055m, actual: small);   // 500/1e6*100 + 50/1e6*100
        Assert.Equal(expected: 15.005m, actual: large);  // 150_000/1e6*100 + 50/1e6*100
    }

    [Fact]
    public async Task RunCycle_ScalesTheBaselineFromTheTurnsOwnObservedInput_NotJustItsPromptText()
    {
        // Guards the fix for the review finding that TranscriptRecord.PromptText is only the newest user
        // message: two turns carrying the *same* short prompt text but very different real input usage must
        // still price differently, or the estimate is being taken from the text fragment again.
        var counter = new TokenCounterRegistry();
        var small = await BaselineCostForTurnAsync(inputTokens: 1_000, tokenCounter: counter, prompt: "same text");
        var large = await BaselineCostForTurnAsync(inputTokens: 100_000, tokenCounter: counter, prompt: "same text");

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.True(condition: large > small * 50,
            userMessage: $"expected observed usage to drive the baseline; got small={small} large={large}");
    }

    [Fact]
    public async Task RunCycle_BothModelsOnAStandInEncoding_RecordsTheRatioAsUnmeasured()
    {
        // model-a and model-b both fall back to cl100k_base, so their ratio is 1.0 by construction. The row
        // must say that was assumed, not measured - otherwise a reader cannot tell this apart from two
        // models that genuinely share a tokenizer.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-b", 0.5, Cost: 0.10m),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b")
        ], tokenCounter: new TokenCounterRegistry());

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = (await harness.ComparisonStore.LoadSinceAsync(since: DateTimeOffset.MinValue,
            cancellationToken: TestContext.Current.CancellationToken)).Last(r => r.RoutedModel == "model-a");
        Assert.Equal(expected: 1d, actual: row.BaselineTokenizerRatio!.Value, tolerance: 0.0001d);
        Assert.False(row.BaselineTokenizerRatioMeasured);
    }

    [Fact]
    public async Task RunCycle_TurnWithNoRecordedUsage_StillPricesFromTheObservedAverage()
    {
        // The fallback path: a turn the provider reported no usage for keeps the previous behavior rather
        // than losing the estimate entirely.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-b", 0.5, Cost: 0.10m),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b",
                InputTokens: 0)
        ], tokenCounter: new TokenCounterRegistry());

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(rows.Last(r => r.RoutedModel == "model-a").BaselineEstimatedCostUsd);
    }

    /// <summary>
    /// Runs one comparison cycle over a single routed turn that consumed <paramref name="inputTokens"/> of
    /// input, and returns the baseline cost the counterfactual estimated for it.
    /// </summary>
    /// <param name="inputTokens">The turn's observed input token usage.</param>
    /// <param name="tokenCounter">The counter to wire, or <see langword="null"/> for the average fallback.</param>
    /// <param name="prompt">The captured prompt text for the routed turn.</param>
    /// <returns>The estimated baseline cost, or <see langword="null"/> when none was estimable.</returns>
    private async Task<decimal?> BaselineCostForTurnAsync(int inputTokens, ITokenCounter? tokenCounter,
        string prompt = "write a function")
    {
        var harness = await BuildHarnessAsync(
        [
            // Seeds an observed token average for model-b, which the output half of the estimate still needs.
            new Sample(Embedding: [1f, 0f], Model: "model-b", 0.5, Cost: 0.10m),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b",
                PromptText: prompt, InputTokens: inputTokens)
        ], tokenCounter: tokenCounter);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        // Both scenarios in a test share this class instance's database, so rows accumulate across calls;
        // the newest matching row (LoadSinceAsync returns oldest first) is this invocation's.
        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        return rows.Last(r => r.RoutedModel == "model-a").BaselineEstimatedCostUsd;
    }

    [Fact]
    public async Task RunCycle_CheaperRoutedModel_RecordsAPositiveEstimatedSaving()
    {
        // The router served model-a; the untrained baseline would have served the pricier model-b. Both
        // have observed token averages, so the counterfactual is estimable.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-b", 0.5, Cost: 0.10m),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b")
        ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        var routed = Assert.Single(collection: rows, predicate: r => r.RoutedModel == "model-a");
        Assert.Equal(expected: "model-b", actual: routed.BaselineModel);
        Assert.NotNull(routed.EstimatedNetSavingsUsd);
        Assert.True(
            condition: routed.EstimatedNetSavingsUsd > 0,
            userMessage:
            $"Routing to the cheaper model should record a positive saving, got {routed.EstimatedNetSavingsUsd}.");

        // The converse row is the honest mirror image: serving the pricier model where the baseline's own
        // observed averages were cheaper records a loss, not a floor at zero.
        var lost = Assert.Single(collection: rows, predicate: r => r.RoutedModel == "model-b");
        Assert.True(lost.EstimatedNetSavingsUsd < 0);
    }

    [Fact]
    public async Task RunCycle_IsIdempotent_ReprocessingProducesNoDuplicateRows()
    {
        var harness = await BuildHarnessAsync([new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(rows);
    }

    [Fact]
    public async Task RunCycle_TranscriptCaptureDisabled_DoesNothing()
    {
        var harness = await BuildHarnessAsync(samples: [new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9)],
            transcriptsEnabled: false);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunCycle_UnscoredRowsAreNotYetEligible()
    {
        var harness = await BuildHarnessAsync([new Sample(Embedding: [1f, 0f], Model: "model-a", null)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        // A row with no verifier score has nothing for either taxonomy to be measured against.
        Assert.Empty(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunCycle_RecordsRegretFromFrozenPriorAndRewardWeights()
    {
        // model-b's frozen probing-prior average (0.5) is what the untrained baseline predicts - read
        // directly from the synced corpus, never from live memory; the routed row scored 0.9 at $0.01
        // against a much pricier counterfactual, so regret must be negative (the untrained baseline would
        // likely have done worse AND cost more) and exactly the reward difference under the default
        // (ε₁, ε₂) = (1, -0.1) weights.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-b", 0.5, Cost: 0.10m),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b")
        ],
        priorRows: [new PriorRow(Dimension: "code_generation", Model: "model-b", Score: 0.5)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        var routed = Assert.Single(collection: rows, predicate: r => r.RoutedModel == "model-a");

        Assert.NotNull(routed.BaselinePredictedScore);
        Assert.Equal(0.5, actual: routed.BaselinePredictedScore!.Value, 10);

        Assert.NotNull(routed.EstimatedRegret);
        var expectedRegret =
            1.0 * routed.BaselinePredictedScore.Value + -0.1 * (double)routed.BaselineEstimatedCostUsd!.Value
            - (1.0 * 0.9 + -0.1 * 0.01);
        Assert.Equal(expected: expectedRegret, actual: routed.EstimatedRegret!.Value, 10);
        Assert.True(condition: routed.EstimatedRegret < 0,
            userMessage:
            $"Routing beat the baseline on both axes; regret should be negative, got {routed.EstimatedRegret}.");
    }

    // Regression coverage for docs/router/routing-roi-regret-plan.md's frozen-baseline correction, second
    // pass: the transcript's own UntrainedBaselinePredictedScore (captured by RequestInterceptor from the
    // prior snapshot in force at selection time) must win over priorMatrix even when a synced corpus is
    // present and disagrees - simulating a benchmark sync that landed a different average for model-b
    // between the request and this comparison cycle. Before that fix, PredictBaselineScore always
    // re-derived the score from whatever prior this cycle loaded, silently pairing the request-time model
    // with a comparison-time score.
    [Fact]
    public async Task RunCycle_PrefersTheTranscriptsOwnPredictedScore_OverAPriorThatHasSinceMoved()
    {
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b",
                UntrainedBaselinePredictedScore: 0.5)
        ],
        // A sync that landed after selection: the corpus now says model-b averages 0.9, not the 0.5 the
        // transcript recorded at request time.
        priorRows: [new PriorRow(Dimension: "code_generation", Model: "model-b", Score: 0.9)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.5, actual: row.BaselinePredictedScore!.Value, 10);
    }

    // Companion to the test above: a row written before this column existed (or whose selector abstained
    // on the score) has no request-time score to prefer, so PredictBaselineScore must still fall back to
    // priorMatrix rather than going permanently null.
    [Fact]
    public async Task RunCycle_FallsBackToThePriorMatrix_WhenTheTranscriptHasNoPredictedScoreOfItsOwn()
    {
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Cost: 0.01m, UntrainedBaselineModel: "model-b",
                UntrainedBaselinePredictedScore: null)
        ],
        priorRows: [new PriorRow(Dimension: "code_generation", Model: "model-b", Score: 0.5)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.5, actual: row.BaselinePredictedScore!.Value, 10);
    }

    [Fact]
    public async Task RunCycle_UnpriceableBaseline_RecordsNullRegretOnce()
    {
        // model-c has no price, no observed token averages, and no ledger cell - every regret input is
        // missing, so both estimates stay null. One shot: the comparison row exists, so a second cycle
        // must not requeue or duplicate it.
        var harness = await BuildHarnessAsync(
            [new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, UntrainedBaselineModel: "model-c")]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(expected: "model-c", actual: row.BaselineModel);
        Assert.Null(row.BaselinePredictedScore);
        Assert.Null(row.EstimatedRegret);
        Assert.Null(row.EstimatedNetSavingsUsd);
    }

    [Fact]
    public async Task RunCycle_BaselinePrediction_NeverReadsLiveMemory_OnlyTheFrozenPrior()
    {
        // The routed model IS the baseline's pick, and live memory ends up holding two observations for it
        // (0.2, 0.8) by the time the cycle runs. Under the old dim_best-voter blend this self-contamination
        // required a leave-one-out correction. The untrained baseline reads none of that live memory at
        // all - both rows' predicted baseline score must be exactly the frozen prior's average (0.4),
        // completely unaffected by what live traffic recorded for the very same model
        // (docs/router/routing-roi-regret-plan.md's frozen-baseline correction).
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.2, UntrainedBaselineModel: "model-a"),
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.8, UntrainedBaselineModel: "model-a")
        ],
        priorRows: [new PriorRow(Dimension: "code_generation", Model: "model-a", Score: 0.4)]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, actual: rows.Count);
        Assert.All(collection: rows,
            action: r => Assert.Equal(0.4, actual: r.BaselinePredictedScore!.Value, 10));
    }

    [Fact]
    public async Task RunCycle_BacklogSpansMultipleBatches_DrainsEveryPendingRow()
    {
        var harness = await BuildHarnessAsync(
            samples:
            [
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.8),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.7),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.6),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.5)
            ],
            batchSize: 2);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        // One cycle drains the whole backlog - three fetches at batch size 2, not one batch per tick.
        var rows = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, actual: rows.Count);
    }

    [Fact]
    public async Task RunCycle_RowWithNoDimension_IsNeverQueuedAndTheCycleTerminates()
    {
        // A dimensionless row is not comparable against the frozen taxonomy, and with oldest-first
        // ordering it would sit at the head of every batch: the queue predicate must exclude it, so the
        // cycle both terminates and still compares the row behind it.
        var harness = await BuildHarnessAsync(
        [
            new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9, Dimension: null),
            new Sample(Embedding: [0f, 1f], Model: "model-a", 0.7)
        ]);

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.7, actual: row.ObservedScore);
    }

    [Fact]
    public async Task RunCycle_RequestsInFlight_DoesNothing()
    {
        var gauge = new InFlightRequestGauge();
        var harness = await BuildHarnessAsync(samples: [new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9)],
            inFlightGauge: gauge);

        using (gauge.Track())
        {
            await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        }

        // The hard-pause guarantee: with a request in flight, the cycle does no comparison work at all.
        Assert.Empty(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Single(await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunCycle_TrafficArrivesMidDrain_StopsAndResumesNextCycle()
    {
        // The tripping store raises the gauge after the second transcript read, simulating a request
        // arriving mid-drain: the first cycle commits the batch it was in and stops before the next row;
        // the second (idle) cycle finishes the backlog.
        var harness = await BuildHarnessAsync(
            samples:
            [
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.9),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.8),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.7),
                new Sample(Embedding: [1f, 0f], Model: "model-a", 0.6)
            ],
            batchSize: 2,
            wrapTranscriptStore: (gauge, inner) => new GaugeTrippingTranscriptStore(inner: inner, gauge: gauge, 2));

        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var afterFirstCycle = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, actual: afterFirstCycle.Count);

        harness.Gauge!.Decrement();
        await harness.Service.RunCycleAsync(TestContext.Current.CancellationToken);

        var afterSecondCycle = await harness.ComparisonStore.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, actual: afterSecondCycle.Count);
    }

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
        Func<InFlightRequestGauge, ITranscriptStore, ITranscriptStore>? wrapTranscriptStore = null,
        IReadOnlyList<PriorRow>? priorRows = null,
        ITokenCounter? tokenCounter = null)
    {
        var storageOptions = Options.Create(new StorageOptions
        {
            TranscriptDatabasePath = _dbPath,
            ClusterModelPath = _clusterModelPath
        });
        var transcriptOptions = Options.Create(new TranscriptOptions { Enabled = transcriptsEnabled });
        var transcriptOptionsMonitor =
            new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = transcriptsEnabled });

        var database = new TranscriptDatabase(storageOptions);
        database.EnsureCreated();

        var transcriptStore = new SqliteTranscriptStore(database: database, options: transcriptOptionsMonitor);
        var comparisonStore = new SqliteTaxonomyComparisonStore(database: database, options: transcriptOptions);
        var memoryEntryStore = new InMemoryEntryStore();
        var routerMemory = new RouterMemory();

        var token = TestContext.Current.CancellationToken;
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var entry = await memoryEntryStore.AppendAsync(
                entry: new MemoryEntry(
                    0,
                    TaskEmbedding: sample.Embedding,
                    ChosenModel: sample.Model,
                    Score: sample.Score ?? 0,
                    0,
                    null,
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    IsExploratory: sample.IsExploratory),
                cancellationToken: token);

            var id = await transcriptStore.InsertAsync(
                record: new TranscriptRecord(
                    0,
                    CorrelationId: $"session-1:{i}",
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    RequestedModel: "auto",
                    RoutedModel: sample.Model,
                    Dimension: sample.Dimension,
                    Difficulty: "medium",
                    Language: "python",
                    false,
                    PromptText: sample.PromptText,
                    ResponseText: "def f(): ...",
                    null,
                    Cost: sample.Cost,
                    IsExploratory: sample.IsExploratory,
                    1.0,
                    sample.InputTokens,
                    50,
                    null,
                    UntrainedBaselineModel: sample.UntrainedBaselineModel,
                    UntrainedBaselinePredictedScore: sample.UntrainedBaselinePredictedScore),
                cancellationToken: token);

            if (id is not null)
            {
                await transcriptStore.LinkMemoryEntryAsync(transcriptId: id.Value, memoryEntryId: entry.Id,
                    cancellationToken: token);
                if (sample.Score is { } score)
                {
                    await transcriptStore.UpdateOutcomeAsync(correlationId: $"session-1:{i}", score: score,
                        cancellationToken: token);
                    if (sample.Dimension is { } sampleDimension)
                        // Mirrors RouterMemoryScoreObserver: live memory is keyed by the live-prefixed dimension.
                        await routerMemory.AddScoreAsync(
                            dimension: RouterDimension.ToLiveKey(liveMemoryPrefix: Prefix, dimension: sampleDimension),
                            model: sample.Model, score: score);
                }
            }
        }

        if (trainClusterModel) WriteClusterModel();

        // No prior rows -> point at a corpus file that never gets created, matching a machine where the
        // benchmark has never been synced (DimensionModelScoreMatrix.SelectBest/AverageScore then always
        // return null, so the untrained-baseline predicted score is null too - the same degrade
        // UntrainedBaselineSelector and DimBestVoter both perform). Prior rows given -> a real, synced
        // corpus backing the frozen probing-split prior the untrained baseline predicts from.
        var benchmarkDbPath = Path.Combine(path1: _tempDirectory, path2: "coderouterbench.db");
        var benchmarkDatabase = new BenchmarkDatabase(Options.Create(new StorageOptions
        {
            BenchmarkDatabasePath = priorRows is { Count: > 0 } ? benchmarkDbPath : "no-such-corpus.db"
        }));
        if (priorRows is { Count: > 0 })
        {
            benchmarkDatabase.EnsureCreated();
            await using var connection = benchmarkDatabase.OpenConnection();
            foreach (var row in priorRows)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                                      INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
                                      VALUES ($taskId, 'probing', 'probing', $dimension, $model, $score);
                                      """;
                command.Parameters.AddWithValue(parameterName: "$taskId", value: Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue(parameterName: "$dimension", value: row.Dimension);
                command.Parameters.AddWithValue(parameterName: "$model", value: row.Model);
                command.Parameters.AddWithValue(parameterName: "$score", value: row.Score);
                command.ExecuteNonQuery();
            }
        }

        inFlightGauge ??= wrapTranscriptStore is null ? null : new InFlightRequestGauge();
        var serviceTranscriptStore = wrapTranscriptStore is null
            ? transcriptStore
            : wrapTranscriptStore(arg1: inFlightGauge!, arg2: transcriptStore);

        var service = new TaxonomyComparisonService(
            logger: NullLogger<TaxonomyComparisonService>.Instance,
            transcriptStore: serviceTranscriptStore,
            comparisonStore: comparisonStore,
            memoryEntryStore: memoryEntryStore,
            routerMemory: routerMemory,
            benchmarkDatabase: benchmarkDatabase,
            routeResolver: new StubRouteResolver(),
            transcriptOptions: transcriptOptions,
            routingOptions: Options.Create(new RoutingOptions { ClusterAssignmentThreshold = 0.5 }),
            storageOptions: storageOptions,
            qualityOptions: Options.Create(new QualityOptions { LiveMemoryPrefix = Prefix }),
            priceLookup: new StubPriceLookup(),
            inFlightGauge: inFlightGauge,
            comparisonBatchSize: batchSize,
            tokenCounter: tokenCounter);

        return new Harness(Service: service, ComparisonStore: comparisonStore, Gauge: inFlightGauge);
    }

    /// <summary>Writes a two-centroid artifact on the axes the fixture embeddings sit on.</summary>
    private void WriteClusterModel()
    {
        var artifact = new ClusterModelArtifact(
            2,
            Centroids: [[1f, 0f], [0f, 1f]],
            2,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [2, 2],
            ClusterDimensionHistograms: [new Dictionary<string, int>(), new Dictionary<string, int>()],
            ClusterTopTerms: [[], []],
            TrainedFrom: "test",
            0,
            4);

        File.WriteAllText(path: _clusterModelPath, contents: ClusterModelArtifactSerializer.Serialize(artifact));
    }

    /// <summary>One fixture request: its embedding, the model that served it, and what came back.</summary>
    private sealed record Sample(
        float[] Embedding,
        string Model,
        double? Score,
        bool IsExploratory = false,
        decimal? Cost = 0.05m,
        string? UntrainedBaselineModel = "model-b",
        string? Dimension = "code_generation",
        double? UntrainedBaselinePredictedScore = null,
        string PromptText = "write a function",
        int InputTokens = 100);

    /// <summary>One row of a fixture's synced frozen probing-split prior.</summary>
    private sealed record PriorRow(string Dimension, string Model, double Score);

    /// <summary>Everything one test needs to drive a cycle and inspect its output.</summary>
    private sealed record Harness(
        TaxonomyComparisonService Service,
        ITaxonomyComparisonStore ComparisonStore,
        InFlightRequestGauge? Gauge);

    /// <summary>
    /// Delegates everything to the real store, but raises <paramref name="gauge"/> after the
    /// <paramref name="tripAfterReads"/>-th transcript read - the seam that lets a test make "a proxy
    /// request arrives" happen at an exact point inside a running drain.
    /// </summary>
    private sealed class GaugeTrippingTranscriptStore(
        ITranscriptStore inner,
        InFlightRequestGauge gauge,
        int tripAfterReads) : ITranscriptStore
    {
        private int _reads;

        public async Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            var record = await inner.GetTranscriptAsync(id: id, cancellationToken: cancellationToken);
            if (++_reads == tripAfterReads) gauge.Increment();

            return record;
        }

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            return inner.InsertAsync(record: record, cancellationToken: cancellationToken);
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            return inner.UpdateOutcomeAsync(correlationId: correlationId, score: score,
                cancellationToken: cancellationToken);
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return inner.LoadUnembeddedScoredAsync(limit: limit, cancellationToken: cancellationToken);
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            return inner.LinkMemoryEntryAsync(transcriptId: transcriptId, memoryEntryId: memoryEntryId,
                cancellationToken: cancellationToken);
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return inner.GetRowCountAsync(cancellationToken);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            return inner.DeleteOldestAsync(count: count, cancellationToken: cancellationToken);
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            return inner.DeleteBeforeAsync(cutoff: cutoff, cancellationToken: cancellationToken);
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            return inner.DeleteAllAsync(cancellationToken);
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            return inner.LoadPromptTextByMemoryEntryIdAsync(cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            return inner.LoadObservedTokenAveragesAsync(cancellationToken);
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            return inner.LoadPendingQualityRescanAsync(scorerVersion: scorerVersion, limit: limit,
                cancellationToken: cancellationToken);
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            return inner.MarkQualityRescannedAsync(transcriptId: transcriptId, scorerVersion: scorerVersion,
                score: score, cancellationToken: cancellationToken);
        }
    }

    /// <summary>An in-memory <see cref="IMemoryEntryStore"/>, avoiding a second SQLite file per test.</summary>
    private sealed class InMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);
        }

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
                ModelName: modelName, Provider: "openai", ProviderModelId: modelName,
                UpstreamBaseUrl: new Uri("https://example.invalid"), AuthHeaderName: "Authorization", ExtraHeaders: []);
            return true;
        }

        public IReadOnlyList<AvailableModel> ListModels()
        {
            return [];
        }

        public bool IsProviderEnabled(string provider)
        {
            return true;
        }

        public bool IsModelEnabled(string modelName)
        {
            return true;
        }
    }

    /// <summary>Prices model-b well above model-a so a routing saving is unambiguous.</summary>
    private sealed class StubPriceLookup : IModelPriceLookup
    {
        public ModelPrice? TryGetPrice(ModelKey key)
        {
            return key.ModelName switch
            {
                "model-a" => new ModelPrice(1m, 1m),
                "model-b" => new ModelPrice(100m, 100m),
                _ => null
            };
        }
    }
}