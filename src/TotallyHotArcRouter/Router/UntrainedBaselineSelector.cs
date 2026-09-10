using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Selects the model an untrained router - one that has never seen live traffic - would have picked for
/// a request, using only the frozen CodeRouterBench probing-split prior
/// (<see cref="DimensionModelScoreMatrix.SelectBest"/>). This is the ROI cost-savings yardstick's
/// "what if we hadn't routed" alternative (docs/router/routing-roi-regret-plan.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists separately from <see cref="Orchestrator.DimBestVoter"/>.</b> That voter blends the
/// frozen prior with live <see cref="RouterMemory"/>, preferring live the moment any observation exists -
/// exactly the behavior that makes it unusable as a savings yardstick, because a baseline that learns
/// alongside the router it is measured against holds the gap between them constant and reports an
/// improvement of zero regardless of how much the router has actually learned. This selector reads the
/// prior alone and never touches <see cref="RouterMemory"/>, so its pick for a given (dimension,
/// candidate-set) pair never changes as traffic accumulates.
/// </para>
/// <para>
/// <b>Degrade path mirrors <see cref="Orchestrator.DimBestVoter"/>'s exactly</b> - an unsynced or
/// unreadable corpus yields <see langword="null"/> ("no untrained baseline for this request") rather
/// than throwing, so a missing corpus never breaks request handling.
/// </para>
/// </remarks>
public sealed class UntrainedBaselineSelector
{
    private readonly BenchmarkDatabase _database;
    private readonly ILogger<UntrainedBaselineSelector> _logger;
    private readonly Lock _matrixLock = new();
    private DimensionModelScoreMatrix? _matrix;
    private bool _matrixLoaded;
    private DateTime _matrixStamp;

    /// <summary>Initializes a new instance of the <see cref="UntrainedBaselineSelector"/> class.</summary>
    /// <param name="database">The synced CodeRouterBench corpus.</param>
    /// <param name="logger">The logger.</param>
    public UntrainedBaselineSelector(BenchmarkDatabase database, ILogger<UntrainedBaselineSelector> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Picks the untrained baseline's model for <paramref name="dimension"/> from
    /// <paramref name="candidateModelIds"/>.
    /// </summary>
    /// <param name="dimension">The request's dimension.</param>
    /// <param name="candidateModelIds">The models actually available for this request.</param>
    /// <returns>
    /// The untrained baseline's pick, or <see langword="null"/> when the corpus is unsynced/unreadable or
    /// has no average for any candidate.
    /// </returns>
    public string? Select(string dimension, IEnumerable<string> candidateModelIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentNullException.ThrowIfNull(candidateModelIds);

        var matrix = EnsureMatrixLoaded();
        return matrix?.SelectBest(dimension: dimension, candidateModelIds: candidateModelIds);
    }

    /// <summary>
    /// Loads the probing-split matrix, caching it keyed on the corpus file's last write time -
    /// the same freshness check <see cref="Transcripts.TaxonomyComparisonService.LoadPriorMatrix"/> uses -
    /// so an explicit benchmark sync while the process is running is picked up on the next request
    /// instead of being masked forever by a cached miss or a stale matrix.
    /// </summary>
    private DimensionModelScoreMatrix? EnsureMatrixLoaded()
    {
        lock (_matrixLock)
        {
            var stamp = File.Exists(_database.DatabasePath)
                ? File.GetLastWriteTimeUtc(_database.DatabasePath)
                : DateTime.MinValue;
            if (_matrixLoaded && stamp == _matrixStamp) return _matrix;

            _matrixStamp = stamp;
            _matrixLoaded = true;
            _matrix = LoadMatrix(stamp);
            return _matrix;
        }
    }

    /// <summary>
    /// Reads the probing-split prior from the CodeRouterBench corpus, returning <see langword="null"/>
    /// when the corpus is not synced on this machine or cannot be read - the same degrade
    /// <see cref="Orchestrator.DimBestVoter.LoadPriorMatrix"/> performs.
    /// </summary>
    /// <param name="stamp">
    /// The corpus file's last write time as observed by <see cref="EnsureMatrixLoaded"/>, or
    /// <see cref="DateTime.MinValue"/> when the corpus does not exist.
    /// </param>
    private DimensionModelScoreMatrix? LoadMatrix(DateTime stamp)
    {
        if (stamp == DateTime.MinValue)
        {
            _logger.LogInformation(
                message:
                "Untrained-baseline selector found no synced CodeRouterBench corpus at {DatabasePath}; ROI savings will not estimate a baseline.",
                _database.DatabasePath);
            return null;
        }

        try
        {
            return DimensionModelScoreMatrix.FromDatabase(database: _database, split: "probing");
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(
                exception: ex,
                message: "Untrained-baseline selector could not read the CodeRouterBench corpus.");
            return null;
        }
    }
}
