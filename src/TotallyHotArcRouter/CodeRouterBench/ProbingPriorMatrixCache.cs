using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Caches the frozen CodeRouterBench probing-split prior (<see cref="DimensionModelScoreMatrix"/>) built
/// from <see cref="BenchmarkDatabase"/>, shared across every reader of it -
/// <see cref="Router.Orchestrator.DimBestVoter"/>, <see cref="Router.UntrainedBaselineSelector"/>, and
/// <see cref="Transcripts.TaxonomyComparisonService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> All three readers above independently called
/// <see cref="DimensionModelScoreMatrix.FromDatabase"/> - the same full table scan of
/// <c>benchmark_id_results</c> filtered to <c>split = "probing"</c> - each keeping its own private copy.
/// On a live request, <c>OrchestratorRoutingPolicy</c> votes through <c>DimBestVoter</c> and
/// <c>RequestInterceptor</c> separately calls <c>UntrainedBaselineSelector</c> for the ROI baseline, both
/// within the same request's call stack; whichever singleton's first request happened to arrive first paid
/// the scan, and the other paid it again independently. Registered as a DI singleton and injected into all
/// three readers, this cache makes only the very first caller (across the whole process, per corpus
/// version) pay the scan - every other call, from any reader, is a cheap in-memory hit.
/// </para>
/// <para>
/// <b>Freshness.</b> Cached keyed on <see cref="BenchmarkDatabase.GetContentStamp"/>, so an explicit
/// benchmark sync while the process is running is picked up by every reader on their next call, rather
/// than being masked forever by a cached miss or a stale matrix - the same guarantee each of the three
/// readers previously implemented separately (and, in <c>DimBestVoter</c>'s case, didn't implement at all:
/// it cached its first-loaded matrix - or lack of one - for the process's lifetime).
/// </para>
/// </remarks>
public sealed class ProbingPriorMatrixCache
{
    private readonly BenchmarkDatabase _database;
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;
    private DimensionModelScoreMatrix? _matrix;
    private bool _loaded;
    private DateTime _stamp;

    /// <summary>Initializes a new instance of the <see cref="ProbingPriorMatrixCache"/> class.</summary>
    /// <param name="database">The CodeRouterBench corpus database backing the probing-split prior.</param>
    /// <param name="logger">
    /// Logs when the corpus is unsynced or unreadable. Optional - a non-generic <see cref="ILogger"/>
    /// rather than <c>ILogger&lt;ProbingPriorMatrixCache&gt;</c> so a reader falling back to a private
    /// instance (see e.g. <see cref="Router.UntrainedBaselineSelector"/>'s constructor) can pass its own
    /// already-typed logger straight through instead of needing a second, differently-typed one.
    /// </param>
    public ProbingPriorMatrixCache(BenchmarkDatabase database, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Gets the probing-split prior, loading and caching it on first use (or after the corpus changes).
    /// </summary>
    /// <returns>
    /// The probing-split matrix, or <see langword="null"/> when the corpus is unsynced on this machine or
    /// cannot be read.
    /// </returns>
    public DimensionModelScoreMatrix? GetMatrix()
    {
        lock (_lock)
        {
            var stamp = _database.GetContentStamp();
            if (_loaded && stamp == _stamp) return _matrix;

            _stamp = stamp;
            _loaded = true;
            _matrix = Load(stamp);
            return _matrix;
        }
    }

    /// <summary>
    /// Reads the probing-split prior from the CodeRouterBench corpus, returning <see langword="null"/> when
    /// the corpus is not synced on this machine or cannot be read.
    /// </summary>
    /// <param name="stamp">
    /// The corpus's content stamp as observed by <see cref="GetMatrix"/>, or <see cref="DateTime.MinValue"/>
    /// when the corpus does not exist.
    /// </param>
    private DimensionModelScoreMatrix? Load(DateTime stamp)
    {
        if (stamp == DateTime.MinValue)
        {
            _logger?.LogInformation(
                message:
                "Probing-prior cache found no synced CodeRouterBench corpus at {DatabasePath}; every reader (dim_best voting, the untrained ROI baseline, taxonomy comparison) will see no prior until a sync completes.",
                _database.DatabasePath);
            return null;
        }

        try
        {
            return DimensionModelScoreMatrix.FromDatabase(database: _database, split: "probing");
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(exception: ex,
                message: "Probing-prior cache could not read the CodeRouterBench corpus.");
            return null;
        }
    }
}
