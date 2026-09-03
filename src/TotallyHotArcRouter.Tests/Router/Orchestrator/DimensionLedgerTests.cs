using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers the frozen nine-dimension taxonomy's ledger
/// (docs/router/self-organizing-classification-plan.md Phase T4) - both the blend rule it inherited from
/// <c>DimBestVoter</c> and the leave-one-out prediction the MAE comparison scores against.
/// </summary>
public sealed class DimensionLedgerTests
{
    private const string Prefix = "live:";

    /// <summary>Builds a probing-prior matrix from inline rows, avoiding a temp benchmark database.</summary>
    private static DimensionModelScoreMatrix Prior(params (string Dimension, string Model, double Score)[] rows)
    {
        return DimensionModelScoreMatrix.FromRows(rows.Select((r, i) =>
            new CodeRouterBenchResultRow(TaskId: $"task-{i}", Dimension: r.Dimension, Model: r.Model, Score: r.Score)));
    }

    [Fact]
    public async Task Predict_LiveScorePresent_WinsOverThePrior()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Prefix + "code_generation", model: "model-a", 0.9);
        var ledger = new DimensionLedger(routerMemory: memory, priorMatrix: Prior(("code_generation", "model-a", 0.1)),
            liveMemoryPrefix: Prefix);

        Assert.Equal(0.9, actual: ledger.Predict(dimension: Prefix + "code_generation", model: "model-a")!.Value, 6);
    }

    [Fact]
    public void Predict_NoLiveScore_FallsBackToThePriorUnderTheUnprefixedKey()
    {
        var ledger = new DimensionLedger(routerMemory: new RouterMemory(),
            priorMatrix: Prior(("code_generation", "model-a", 0.4)), liveMemoryPrefix: Prefix);

        // The caller passes a live-prefixed key; the prior was built from unprefixed CodeRouterBench rows,
        // so the ledger must strip the prefix before querying it or the prior would never match.
        Assert.Equal(0.4, actual: ledger.Predict(dimension: Prefix + "code_generation", model: "model-a")!.Value, 6);
    }

    [Fact]
    public void Predict_NeitherSourceHasTheCell_ReturnsNull()
    {
        var ledger = new DimensionLedger(routerMemory: new RouterMemory(), priorMatrix: Prior(),
            liveMemoryPrefix: Prefix);

        Assert.Null(ledger.Predict(dimension: Prefix + "code_generation", model: "model-a"));
    }

    [Fact]
    public void Predict_NoPriorMatrixAtAll_ScoresFromLiveMemoryOnly()
    {
        // An unsynced CodeRouterBench corpus degrades to live-only rather than throwing.
        var ledger = new DimensionLedger(routerMemory: new RouterMemory(), null, liveMemoryPrefix: Prefix);

        Assert.Null(ledger.Predict(dimension: Prefix + "code_generation", model: "model-a"));
    }

    [Fact]
    public async Task PredictLeaveOneOut_RemovesTheObservationFromItsOwnCell()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Prefix + "code_generation", model: "model-a", 0.2);
        await memory.AddScoreAsync(dimension: Prefix + "code_generation", model: "model-a", 0.8);
        var ledger = new DimensionLedger(routerMemory: memory, priorMatrix: Prior(), liveMemoryPrefix: Prefix);

        // Mean of {0.2, 0.8} is 0.5; holding out the 0.8 must leave 0.2, not 0.5.
        var heldOut = ledger.PredictLeaveOneOut(dimension: Prefix + "code_generation", model: "model-a", 0.8);

        Assert.Equal(0.2, actual: heldOut!.Value, 6);
    }

    [Fact]
    public async Task PredictLeaveOneOut_SingleObservationCell_FallsBackToThePrior()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Prefix + "code_generation", model: "model-a", 0.8);
        var ledger = new DimensionLedger(routerMemory: memory, priorMatrix: Prior(("code_generation", "model-a", 0.35)),
            liveMemoryPrefix: Prefix);

        // Nothing is left after holding out the only observation, so the offline prior - which live traffic
        // never writes into, and so needs no correction - answers instead.
        var heldOut = ledger.PredictLeaveOneOut(dimension: Prefix + "code_generation", model: "model-a", 0.8);

        Assert.Equal(0.35, actual: heldOut!.Value, 6);
    }

    [Fact]
    public async Task PredictLeaveOneOut_SingleObservationAndNoPrior_ReturnsNull()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Prefix + "code_generation", model: "model-a", 0.8);
        var ledger = new DimensionLedger(routerMemory: memory, priorMatrix: Prior(), liveMemoryPrefix: Prefix);

        // Excluded from the error series rather than answered with a fabricated number.
        Assert.Null(ledger.PredictLeaveOneOut(dimension: Prefix + "code_generation", model: "model-a", 0.8));
    }

    [Fact]
    public void LiveKeyConventionMatchesWhatTheObserverWrites()
    {
        // The comparison job holds a bare dimension from request_transcripts and must convert it the same
        // way RouterMemoryScoreObserver does when writing, or every live lookup would miss.
        Assert.Equal(expected: Prefix + "code_generation",
            actual: RouterDimension.ToLiveKey(liveMemoryPrefix: Prefix, dimension: "code_generation"));
    }
}