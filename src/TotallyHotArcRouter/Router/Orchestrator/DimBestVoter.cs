using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
/// machine. This voter tolerates that the same way <c>CodeRouterBenchTable10ReconciliationTests</c> does:
/// it checks <see cref="BenchmarkDatabase.DatabasePath"/> for existence before opening a connection (SQLite
/// would otherwise create an empty file as a side effect of connecting), and degrades to live-memory-only
/// scoring rather than throwing when the corpus, or a needed row, is absent.
/// </para>
/// </remarks>
public sealed class DimBestVoter : IRoutingVoter
{
    private readonly BenchmarkDatabase _database;
    private readonly RouterMemory _routerMemory;
    private readonly ILogger<DimBestVoter> _logger;
    private readonly string _liveMemoryPrefix;
    private readonly object _matrixLock = new();
    private DimensionLedger? _ledger;
    private bool _matrixLoadAttempted;

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
    public DimBestVoter(
        BenchmarkDatabase database,
        RouterMemory routerMemory,
        ILogger<DimBestVoter> logger,
        IOptions<QualityOptions> qualityOptions)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(routerMemory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(qualityOptions);

        _database = database;
        _routerMemory = routerMemory;
        _logger = logger;
        _liveMemoryPrefix = qualityOptions.Value.LiveMemoryPrefix;
    }

    /// <inheritdoc />
    public string Name => VoterNames.DimBest;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="VotingContext.Dimension"/> is the <em>live</em> <see cref="RouterMemory"/> key (typically
    /// <c>"live:" + dimension</c>, via <see cref="RouterDimension.ToLiveKey"/>), which is exactly what the
    /// live-memory lookup below needs. <see cref="DimensionModelScoreMatrix.AverageScore"/> instead expects
    /// the bare, unprefixed <see cref="RouterDimension"/> key it was built from - passing the live-prefixed
    /// key there would never match a row, silently degrading this voter to live-memory-only. The prior
    /// lookup below strips <see cref="_liveMemoryPrefix"/> back off first so both sources are queried under
    /// their own convention.
    /// </remarks>
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var ledger = EnsureLedgerLoaded();

        string? bestModel = null;
        var bestScore = double.NegativeInfinity;
        foreach (var candidate in context.Candidates)
        {
            var blended = ledger.Predict(context.Dimension, candidate.ModelName);
            if (blended is null)
            {
                continue;
            }

            if (blended.Value > bestScore)
            {
                bestScore = blended.Value;
                bestModel = candidate.ModelName;
            }
        }

        var vote = bestModel is null
            ? VoterVote.Abstain(Name)
            : new VoterVote(Name, bestModel, Math.Clamp(bestScore, 0d, 1d));
        return Task.FromResult(vote);
    }

    /// <summary>
    /// Builds this voter's <see cref="DimensionLedger"/> on first use, loading the probing-split prior and
    /// tolerating an unsynced or unreadable corpus by building a live-memory-only ledger instead - see the
    /// class remarks.
    /// </summary>
    /// <returns>The cached ledger.</returns>
    private DimensionLedger EnsureLedgerLoaded()
    {
        lock (_matrixLock)
        {
            if (_matrixLoadAttempted)
            {
                return _ledger!;
            }

            _matrixLoadAttempted = true;
            _ledger = new DimensionLedger(_routerMemory, LoadPriorMatrix(), _liveMemoryPrefix);
            return _ledger;
        }
    }

    /// <summary>
    /// Reads the probing-split prior from the CodeRouterBench corpus, returning <see langword="null"/> when
    /// the corpus is not synced on this machine or cannot be read.
    /// </summary>
    /// <returns>The probing-split matrix, or <see langword="null"/> when unavailable.</returns>
    private DimensionModelScoreMatrix? LoadPriorMatrix()
    {
        if (!File.Exists(_database.DatabasePath))
        {
            _logger.LogInformation(
                "dim_best voter found no synced CodeRouterBench corpus at {DatabasePath}; scoring from live RouterMemory only.",
                _database.DatabasePath);
            return null;
        }

        try
        {
            return DimensionModelScoreMatrix.FromDatabase(_database, "probing");
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(
                ex,
                "dim_best voter could not read the CodeRouterBench corpus; scoring from live RouterMemory only.");
            return null;
        }
    }
}
