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
/// <b>Freshness.</b> Cached keyed on the probing file's own <c>benchmark_files.synced_at_utc</c> ledger
/// row (<see cref="BenchmarkFileLedger"/>), not filesystem mtime: that row is written by
/// <c>BenchmarkSyncService</c> transactionally alongside the actual <c>benchmark_id_results</c> rows it
/// commits, and only when a sync's downloaded content differs from what is already recorded (an
/// oid-matched file is skipped entirely, leaving the prior sync's row - and hence this stamp -
/// untouched). A filesystem-mtime stamp was tried first and rejected: on some filesystems two rapid
/// writes can retain an identical timestamp, which would let this cache miss a real sync and serve a
/// stale matrix to every reader indefinitely, exactly the failure this cache exists to prevent. The
/// ledger row has no such gap, since it changes if and only if the committed probing data actually did.
/// </para>
/// <para>
/// <b>No ledger row is not "no data".</b> The database file existing at all is a separate signal from
/// whether the probing file has a ledger row: <see cref="GetMatrix"/> only ever short-circuits to
/// <see langword="null"/> without touching the database when the file itself is absent (avoiding the
/// connect-creates-an-empty-file SQLite side effect). Once the file exists, a missing ledger row is
/// treated as one more valid (if uninformative) freshness value - <see cref="DateTimeOffset.MinValue"/> -
/// and the matrix is still read from whatever <c>benchmark_id_results</c> rows exist, rather than assumed
/// empty. This matters for anything that seeds rows directly rather than through a real sync (every
/// existing unit test in this codebase, and any future backfill/import path that does the same).
/// </para>
/// <para>
/// <b>Warmed off the request path.</b> <see cref="GetMatrix"/> is still a synchronous full-table scan on
/// a cold cache, and <c>RequestInterceptor</c> reaches <c>UntrainedBaselineSelector</c> (hence this cache)
/// during live request resolution - so an unwarmed cache would add a scan's latency to whichever request
/// happens to be first. Two callers warm it off that path instead: <c>StartupHealthCheckHostedService</c>
/// loads it once before Kestrel binds its port (covering "the first request after this process starts"),
/// and <c>BenchmarkSyncService</c> forces a reload at the end of every sync, on the sync operation itself
/// rather than the next reader (covering "the first request after each benchmark sync"). Both warm-ups are
/// best-effort and log-only - a failure there just leaves the next real caller to pay the scan inline, the
/// same degrade this cache already has.
/// </para>
/// </remarks>
public sealed class ProbingPriorMatrixCache
{
    /// <summary>
    /// The probing split's source file name (<see cref="BenchmarkFileSpec.All"/>) - the
    /// <see cref="BenchmarkFileLedger"/> row this cache keys its freshness on.
    /// </summary>
    private const string ProbingFileName = "id_probing_results_long.csv";

    private readonly BenchmarkDatabase _database;
    private readonly BenchmarkFileLedger _ledger;
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;
    private DimensionModelScoreMatrix? _matrix;
    private bool _loaded;
    private DateTimeOffset? _stamp;

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
        _ledger = new BenchmarkFileLedger(database);
        _logger = logger;
    }

    /// <summary>
    /// Gets the probing-split prior, loading and caching it on first use (or after the corpus changes).
    /// </summary>
    /// <returns>
    /// The probing-split matrix, or <see langword="null"/> when the corpus database does not exist on this
    /// machine or cannot be read.
    /// </returns>
    public DimensionModelScoreMatrix? GetMatrix()
    {
        lock (_lock)
        {
            // null uniquely means "the database file itself does not exist" - see the class remarks for
            // why that must stay distinct from GetSyncStamp's own DateTimeOffset.MinValue ("file exists,
            // no ledger row for the probing file yet").
            var stamp = File.Exists(_database.DatabasePath) ? GetSyncStamp() : (DateTimeOffset?)null;
            if (_loaded && stamp == _stamp) return _matrix;

            _stamp = stamp;
            _loaded = true;
            _matrix = Load(stamp);
            return _matrix;
        }
    }

    /// <summary>
    /// Reads the probing file's <see cref="BenchmarkFileLedgerEntry.SyncedAtUtc"/> - see the class remarks
    /// for why this, rather than a filesystem timestamp, is the freshness signal. Only called once
    /// <see cref="GetMatrix"/> has confirmed the database file exists.
    /// </summary>
    /// <returns>
    /// The probing file's last recorded sync time, or <see cref="DateTimeOffset.MinValue"/> when it has
    /// never synced (including when the file exists but predates the <c>benchmark_files</c> table).
    /// </returns>
    private DateTimeOffset GetSyncStamp()
    {
        try
        {
            return _ledger.TryGet(ProbingFileName)?.SyncedAtUtc ?? DateTimeOffset.MinValue;
        }
        catch (SqliteException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Reads the probing-split prior from the CodeRouterBench corpus, returning <see langword="null"/> when
    /// the corpus database does not exist on this machine or cannot be read.
    /// </summary>
    /// <param name="stamp">
    /// The stamp <see cref="GetMatrix"/> observed - <see langword="null"/> when the database file does not
    /// exist, otherwise the probing file's sync stamp (possibly <see cref="DateTimeOffset.MinValue"/> when
    /// it has never synced, which does not by itself mean there is no data to read - see the class
    /// remarks).
    /// </param>
    private DimensionModelScoreMatrix? Load(DateTimeOffset? stamp)
    {
        if (stamp is null)
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
