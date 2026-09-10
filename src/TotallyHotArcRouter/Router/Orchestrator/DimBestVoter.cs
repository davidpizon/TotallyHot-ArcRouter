using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>dim_best</c> voter (PLAN.md Phase L): a DimensionBest lookup from the Phase K/K2 CodeRouterBench
/// probing matrix, refined by live <see cref="RouterMemory"/> averages when they exist for the same
/// (dimension, model) pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Blend rule (documented choice, not from the research doc):</b> for each candidate, prefer the live
/// <see cref="RouterMemory.GetAverageScore"/> when it has at least one observation, and fall back to the
/// probing-set prior from <see cref="DimensionModelScoreMatrix"/> otherwise. This is the simplest rule
/// that satisfies PLAN.md Phase L's "refined by live RouterMemory averages" - live, execution-grounded
/// feedback always wins once it exists, and the offline prior only fills the cold-start gap before any
/// feedback has accumulated for that pair. A weighted blend (e.g. shrinking toward the prior for a small
/// sample count) was considered and rejected for Phase L, when <see cref="RouterMemory"/> exposed only an
/// average and a sample-size-aware rule would have needed a wider change to that type. That obstacle is
/// gone - <see cref="RouterMemory.GetObservationCount"/> now reports the sample size backing each average -
/// but the blend rule here is deliberately unchanged: switching from "live always wins" to a shrinkage
/// rule alters live routing behavior and belongs to whichever phase is prepared to measure the difference,
/// not to the storage refactor that merely made it expressible.
/// </para>
/// <para>
/// The benchmark corpus is synced on demand (<c>data/README.md</c>) and may not be present on a given
/// machine. This voter tolerates that via <see cref="ProbingPriorMatrixCache"/>, which checks
/// <see cref="BenchmarkDatabase.DatabasePath"/> for existence before opening a connection (SQLite would
/// otherwise create an empty file as a side effect of connecting) and returns <see langword="null"/> rather
/// than throwing when the corpus, or a needed row, is absent - <see cref="VoteAsync"/> then degrades to
/// live-memory-only scoring.
/// </para>
/// <para>
/// <b>Shares its prior with other readers.</b> <see cref="ProbingPriorMatrixCache"/> is a DI singleton also
/// used by <see cref="UntrainedBaselineSelector"/> and <see cref="Transcripts.TaxonomyComparisonService"/>,
/// so the underlying full-table scan runs at most once per corpus sync across all three rather than once
/// per reader - a live request that both votes through this voter and separately consults
/// <see cref="UntrainedBaselineSelector"/> for the ROI baseline previously paid that scan twice.
/// </para>
/// </remarks>
public sealed class DimBestVoter : IRoutingVoter
{
    private readonly string _liveMemoryPrefix;
    private readonly ProbingPriorMatrixCache _matrixCache;
    private readonly RouterMemory _routerMemory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DimBestVoter"/> class.
    /// </summary>
    /// <param name="database">The CodeRouterBench corpus database backing the probing-set prior.</param>
    /// <param name="routerMemory">Live per-dimension score averages, preferred over the prior when present.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="qualityOptions">
    /// Carries the live-memory prefix (<see cref="QualityOptions.LiveMemoryPrefix"/>) used to recover the
    /// bare <see cref="RouterDimension"/> key from <see cref="VotingContext.Dimension"/> before querying
    /// the probing-set prior - see <see cref="VoteAsync"/>'s remarks.
    /// </param>
    /// <param name="matrixCache">
    /// The shared probing-prior cache - see the class remarks. DI always supplies the shared singleton;
    /// <see langword="null"/> (the default) falls back to a private instance over
    /// <paramref name="database"/>/<paramref name="logger"/> so existing direct construction (e.g. tests)
    /// keeps compiling and behaving exactly as before this cache existed.
    /// </param>
    public DimBestVoter(
        BenchmarkDatabase database,
        RouterMemory routerMemory,
        ILogger<DimBestVoter> logger,
        IOptions<QualityOptions> qualityOptions,
        ProbingPriorMatrixCache? matrixCache = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(routerMemory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(qualityOptions);

        _routerMemory = routerMemory;
        _liveMemoryPrefix = qualityOptions.Value.LiveMemoryPrefix;
        _matrixCache = matrixCache ?? new ProbingPriorMatrixCache(database: database, logger: logger);
    }

    /// <inheritdoc/>
    public string Name => VoterNames.DimBest;

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="VotingContext.Dimension"/> is the <em>live</em> <see cref="RouterMemory"/> key (typically
    /// <c>"live:" + dimension</c>, via <see cref="RouterDimension.ToLiveKey"/>), which is exactly what the
    /// live-memory lookup below needs. <see cref="DimensionModelScoreMatrix.AverageScore"/> instead expects
    /// the bare, unprefixed <see cref="RouterDimension"/> key it was built from - passing the live-prefixed
    /// key there would never match a row, silently degrading this voter to live-memory-only. The prior
    /// lookup below strips <see cref="_liveMemoryPrefix"/> back off first so both sources are queried under
    /// their own convention.
    /// <para>
    /// Builds a fresh <see cref="DimensionLedger"/> on every call rather than caching one: its constructor
    /// only stores three field references, so this is cheap, and doing so lets a corpus sync's refreshed
    /// prior - via <see cref="ProbingPriorMatrixCache.GetMatrix"/> - reach this voter's very next vote,
    /// instead of the first-loaded prior being locked in for the process's lifetime.
    /// </para>
    /// </remarks>
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var ledger = new DimensionLedger(routerMemory: _routerMemory, priorMatrix: _matrixCache.GetMatrix(),
            liveMemoryPrefix: _liveMemoryPrefix);

        string? bestModel = null;
        var bestScore = double.NegativeInfinity;
        foreach (var candidate in context.Candidates)
        {
            var blended = ledger.Predict(dimension: context.Dimension, model: candidate.ModelName);
            if (blended is null) continue;

            if (blended.Value > bestScore)
            {
                bestScore = blended.Value;
                bestModel = candidate.ModelName;
            }
        }

        var vote = bestModel is null
            ? VoterVote.Abstain(Name)
            : new VoterVote(VoterName: Name, ModelName: bestModel, Confidence: Math.Clamp(value: bestScore, 0d, 1d));
        return Task.FromResult(vote);
    }
}