using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Runs the startup pricing health checks before the proxy accepts requests. Registered ahead of
/// <see cref="ProxyHostedService"/> so the generic host awaits its <see cref="StartAsync"/> first -
/// Kestrel is not bound until checks 1-4 have run.
/// </summary>
/// <remarks>
/// Every check is <em>log-only</em>: a Warning or Error here never blocks startup or the proxy from
/// binding its port. This is distinct from routing eligibility - a model with no fresh price is excluded
/// from auto-selection by D1, in per-request routing, not by this gate. See
/// <c>docs/router/model-price-catalog.md</c>.
/// </remarks>
public sealed class StartupHealthCheckHostedService : IHostedService
{
    // A short, fixed string is enough to force the one-time artifact download and model load; its content
    // is never read back or stored, only its side effect (warming _session/_tokenizer) matters.
    private const string EmbeddingWarmupText = "embedding warm-up";

    // D1's routing floor, reused here only to describe the check-4 condition in the log message.
    private static readonly TimeSpan FreshnessFloor = TimeSpan.FromHours(24);
    private readonly BenchmarkDatabase _benchmarkDatabase;
    private readonly BenchmarkDataStatusService _benchmarkStatusService;
    private readonly ProviderBudgetStore _budgetStore;
    private readonly PriceCatalogDatabase _database;
    private readonly IEmbeddingClient? _embeddingClient;
    private readonly EmbeddingMemory _embeddingMemory;
    private readonly EmbeddingWarmupState? _embeddingWarmupState;
    private readonly PriceCatalogIngestionService _ingestionService;

    private readonly ILogger<StartupHealthCheckHostedService> _logger;
    private readonly PriceSourceRepository _repository;
    private readonly IUsageRollupStore _rollupStore;
    private readonly RouterMemory _routerMemory;
    private readonly RouterMemoryDatabase _routerMemoryDatabase;
    private readonly StorageOptions _storageOptions;
    private readonly PriceSourceToggleStore _toggleStore;
    private readonly ToolCallCapabilityStore _toolCallCapabilityStore;
    private readonly TranscriptDatabase _transcriptDatabase;
    private readonly TranscriptOptions _transcriptOptions;
    private readonly ITranscriptStore _transcriptStore;
    private readonly IUsageLedger _usageLedger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupHealthCheckHostedService"/> class.
    /// </summary>
    public StartupHealthCheckHostedService(
        ILogger<StartupHealthCheckHostedService> logger,
        PriceCatalogDatabase database,
        PriceSourceRepository repository,
        PriceCatalogIngestionService ingestionService,
        PriceSourceToggleStore toggleStore,
        ProviderBudgetStore budgetStore,
        ToolCallCapabilityStore toolCallCapabilityStore,
        IUsageLedger usageLedger,
        IUsageRollupStore rollupStore,
        IOptions<StorageOptions> storageOptions,
        RouterMemoryDatabase routerMemoryDatabase,
        RouterMemory routerMemory,
        EmbeddingMemory embeddingMemory,
        BenchmarkDatabase benchmarkDatabase,
        BenchmarkDataStatusService benchmarkStatusService,
        TranscriptDatabase transcriptDatabase,
        ITranscriptStore transcriptStore,
        IOptions<TranscriptOptions> transcriptOptions,
        IEmbeddingClient? embeddingClient = null,
        EmbeddingWarmupState? embeddingWarmupState = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(ingestionService);
        ArgumentNullException.ThrowIfNull(toggleStore);
        ArgumentNullException.ThrowIfNull(budgetStore);
        ArgumentNullException.ThrowIfNull(toolCallCapabilityStore);
        ArgumentNullException.ThrowIfNull(usageLedger);
        ArgumentNullException.ThrowIfNull(rollupStore);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(routerMemoryDatabase);
        ArgumentNullException.ThrowIfNull(routerMemory);
        ArgumentNullException.ThrowIfNull(embeddingMemory);
        ArgumentNullException.ThrowIfNull(benchmarkDatabase);
        ArgumentNullException.ThrowIfNull(benchmarkStatusService);
        ArgumentNullException.ThrowIfNull(transcriptDatabase);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(transcriptOptions);

        _logger = logger;
        _database = database;
        _repository = repository;
        _ingestionService = ingestionService;
        _toggleStore = toggleStore;
        _budgetStore = budgetStore;
        _toolCallCapabilityStore = toolCallCapabilityStore;
        _usageLedger = usageLedger;
        _rollupStore = rollupStore;
        _storageOptions = storageOptions.Value;
        _routerMemoryDatabase = routerMemoryDatabase;
        _routerMemory = routerMemory;
        _embeddingMemory = embeddingMemory;
        _benchmarkDatabase = benchmarkDatabase;
        _benchmarkStatusService = benchmarkStatusService;
        _transcriptDatabase = transcriptDatabase;
        _transcriptStore = transcriptStore;
        _transcriptOptions = transcriptOptions.Value;
        _embeddingClient = embeddingClient;
        _embeddingWarmupState = embeddingWarmupState;
    }

    /// <summary>
    /// The background embedding client warm-up task started by <see cref="StartAsync"/>, or
    /// <see langword="null"/> if no embedding client was configured. <see cref="StartAsync"/> does not
    /// await this task itself (see the comment at its call site); exposed internally so tests can await it
    /// deterministically instead of racing the fire-and-forget warm-up.
    /// </summary>
    internal Task? EmbeddingWarmupTask { get; private set; }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Startup pricing health checks are running.");

        // Check 0: adopt any pre-%ProgramData% per-user copy of the storage files before anything opens
        // them. Must precede every EnsureCreated below - those create an empty file at the destination,
        // and the migration deliberately refuses to overwrite a destination that already exists.
        LegacyStorageMigration.Run(options: _storageOptions, logger: _logger);

        // Check 1: ensure the SQLite database exists, creating it (and its directory) if absent. This also
        // applies additive column migrations and seeds a row for every source that has a client, so it must
        // precede the toggle store's first read below.
        var alreadyExisted = _database.EnsureCreated();
        if (alreadyExisted)
            _logger.LogInformation(message: "Found existing pricing database at {Path}.", _database.DatabasePath);
        else
            _logger.LogInformation(message: "Created new pricing database at {Path}.", _database.DatabasePath);

        // The toggle store starts empty by design (its schema may not exist at construction time), so this
        // is what puts it into service. Every source reads as disabled until this runs.
        _toggleStore.Reload();

        // Same story for the per-provider budget store: it starts empty (the provider_budgets/provider_spend
        // tables may not exist at construction), so this first load is what makes caps and current-month
        // spend visible to routing enforcement and the Governance budget bars.
        _budgetStore.Reload();

        // And for the tool-call capability store, for the same reason. Until this runs every model reads as
        // unclassified, which is the safe direction: "unknown" means "forward natively and observe", so a
        // request arriving before this point behaves exactly as it does today rather than being scanned
        // under a dialect that hasn't been loaded.
        _toolCallCapabilityStore.Reload();

        // Check 2: warn when no source is enabled. Read from the database rather than configuration - the
        // toggle is owned by aggregator_sources.enabled and may have been switched off from the Governance
        // panel in a previous run (D6).
        var hasEnabledSource = _toggleStore.List().Any(source => source.Enabled);
        if (!hasEnabledSource)
            _logger.LogWarning(
                "No pricing data sources are enabled; cost estimates will be unavailable for all paid models.");

        // Check 3: if at least one source is enabled, attempt a fresh pull. RunCycleAsync itself logs the
        // zero-fresh-prices Error (D4) when a cycle that ran ends with nothing fresh, so check 4 below
        // only has to cover the no-sources-ran branch.
        var ranCycle = false;
        if (hasEnabledSource)
        {
            await _ingestionService.RunCycleAsync(cancellationToken).ConfigureAwait(false);
            ranCycle = true;
        }

        // Check 4: no manual prices AND no fresh fetched prices -> Error. "No manual prices" is always
        // true today: there is no manual-price mechanism in the codebase yet, so this condition reduces to
        // the fetched-freshness check. A future manual-override feature must update this to consult it.
        const bool noManualPricesConfigured = true;
        if (!ranCycle && noManualPricesConfigured && _repository.CountFreshPrices(FreshnessFloor) == 0)
            // Only reached when no source ran (check 2 found none enabled); a cycle that ran already
            // logged this via RunCycleAsync.
            _logger.LogError(
                message:
                "No pricing data is available: no manual prices are configured and all fetched price data is missing or older than {FreshnessHours} hours.",
                FreshnessFloor.TotalHours);

        // Usage-ledger retention sweep (docs/router/token-tracking-implementation-plan.md Phase 2):
        // deletes rows older than Storage:UsageLedgerRetentionDays, keyed on occurred_at_utc. Best-effort
        // and log-only, like every check above - a sweep failure must never block startup.
        try
        {
            // A misconfigured 0 or negative value would put the cutoff at or after "now", turning this
            // destructive startup sweep into "delete the entire ledger" - skip rather than silently wiping
            // out durable usage history over a config mistake.
            if (_storageOptions.UsageLedgerRetentionDays <= 0)
            {
                _logger.LogWarning(
                    message:
                    "Skipping the usage-ledger retention sweep: Storage:UsageLedgerRetentionDays is {RetentionDays}, must be positive.",
                    _storageOptions.UsageLedgerRetentionDays);
            }
            else
            {
                var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_storageOptions.UsageLedgerRetentionDays);
                var deleted = _usageLedger.DeleteOlderThan(cutoff);
                if (deleted > 0)
                    _logger.LogInformation(
                        message:
                        "Usage-ledger retention sweep deleted {DeletedRows} row(s) older than {RetentionDays} days.",
                        deleted,
                        _storageOptions.UsageLedgerRetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(exception: ex, message: "Usage-ledger retention sweep failed; continuing startup.");
        }

        // Usage-rollup bucket timezone + backfill (docs/router/token-tracking-implementation-plan.md
        // Phase 4, §5.3): pins the wall-clock timezone on first run, then rolls forward any usage_ledger
        // entries newer than the last checkpoint - the "buckets missed while down" catch-up. Best-effort and
        // log-only, like every check above; RollForwardAsync already swallows its own failures internally,
        // but the outer guard also covers EnsureBucketTimezone.
        try
        {
            _rollupStore.EnsureBucketTimezone();
            var rolledUp = await _rollupStore.RollForwardAsync(cancellationToken).ConfigureAwait(false);
            if (rolledUp > 0)
                _logger.LogInformation(message: "Usage-rollup backfill applied {EntryCount} ledger entry/entries.",
                    rolledUp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(exception: ex, message: "Usage-rollup backfill failed; continuing startup.");
        }

        // Router memory: ensure the shared SQLite schema exists, then load both working sets - the
        // dimension-keyed score aggregates behind RouterMemory and the task-embedding-keyed entries behind
        // EmbeddingMemory (PLAN.md Phase J). Best-effort and log-only like every check above; a failure
        // must not block the proxy from binding its port, it just leaves the router starting cold.
        //
        // RouterMemory.InitializeAsync had no production caller before this: scores were persisted on every
        // observation and never read back, so accumulated feedback was silently discarded at every restart
        // and dim_best began each run from the CodeRouterBench prior alone. Loading it here is what makes
        // that persistence actually mean something.
        try
        {
            _routerMemoryDatabase.EnsureCreated();
            await _routerMemory.InitializeAsync().ConfigureAwait(false);
            await _embeddingMemory.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(exception: ex, message: "Router memory initialization failed; continuing startup.");
        }

        // docs/router/self-organizing-classification-plan.md Phase T1a: the transcript store's schema is
        // created only when capture is enabled - "with capture disabled (the default), no table is created
        // and nothing is written" is the plan's stated exit criterion, so this deliberately does not follow
        // every other database above in creating its schema unconditionally.
        if (_transcriptOptions.Enabled)
            try
            {
                _transcriptDatabase.EnsureCreated();
                _logger.LogInformation(message: "Transcript capture is enabled; ensured transcript database at {Path}.",
                    _transcriptDatabase.DatabasePath);

                // Phase T1e: run one purge at startup to clean any retention-expired rows from before this restart.
                try
                {
                    var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_transcriptOptions.RetentionDays);
                    var deleted = await _transcriptStore
                        .DeleteBeforeAsync(cutoff: cutoff, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (deleted > 0)
                        _logger.LogInformation(
                            message:
                            "Transcript retention startup purge deleted {DeletedRows} row(s) older than {RetentionDays} days.",
                            deleted,
                            _transcriptOptions.RetentionDays);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(exception: ex,
                        message: "Transcript retention purge failed at startup; continuing.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(exception: ex,
                    message:
                    "Transcript database initialization failed; continuing startup with capture effectively disabled.");
            }

        // Embedding client warm-up (docs/router/live-feedback-learning-plan.md Phase 2b): forces the
        // one-time ~1.3 GB model/tokenizer download and load, off the request path, rather than on a
        // request's first embedding call. Started here but deliberately NOT awaited: a cold download can
        // take minutes, and awaiting it would delay Kestrel binding its port, contradicting every other
        // check's "never block startup" contract. Runs independently of the startup cancellation token so
        // a host-startup timeout can't abandon a partially-downloaded artifact; failure (no network access,
        // disk full) leaves EmbeddingWarmupState.IsWarm false, which RequestInterceptor reads to skip
        // embedding entirely rather than block a request on a cold download.
        if (_embeddingClient is not null && _embeddingWarmupState is not null)
            EmbeddingWarmupTask = WarmUpEmbeddingClientAsync(embeddingClient: _embeddingClient,
                embeddingWarmupState: _embeddingWarmupState);

        // CodeRouterBench corpus freshness (docs/router/coderouterbench-sqlite-migration-plan.md, Phase
        // 3): ensure its own SQLite schema exists and probe Hugging Face for the corpus's
        // Current/Update/CheckFailed state, best-effort and log-only like every check above - a probe
        // failure must never block the proxy from binding its port. BenchmarkDataStatusService.RecheckAsync
        // already downgrades an expected probe failure to CheckFailed rather than throwing, so this
        // try/catch is a backstop for EnsureCreated and any other unexpected failure from RecheckAsync.
        // RecheckAsync does still throw OperationCanceledException for caller cancellation, though, and
        // that must propagate rather than be logged as a startup failure.
        try
        {
            _benchmarkDatabase.EnsureCreated();
            await _benchmarkStatusService.RecheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(exception: ex,
                message: "CodeRouterBench corpus initialization failed; continuing startup.");
        }

        _logger.LogInformation("Startup pricing health checks complete.");
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs one embedding call against <paramref name="embeddingClient"/> to force the one-time model
    /// download/load off the request path, then marks <paramref name="embeddingWarmupState"/> warm on
    /// success. Started fire-and-forget by <see cref="StartAsync"/> - see the call site's remarks for why
    /// it is not awaited there - so any failure is only logged here; it never propagates to a caller.
    /// </summary>
    /// <param name="embeddingClient">The embedding client to warm up.</param>
    /// <param name="embeddingWarmupState">The shared state flipped to warm on a successful embed call.</param>
    private async Task WarmUpEmbeddingClientAsync(
        IEmbeddingClient embeddingClient,
        EmbeddingWarmupState embeddingWarmupState)
    {
        try
        {
            await embeddingClient.EmbedAsync(EmbeddingWarmupText).ConfigureAwait(false);
            embeddingWarmupState.MarkWarm();
            _logger.LogInformation("Embedding client warm-up complete.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(exception: ex,
                message:
                "Embedding client warm-up failed; embedding-dependent routing signals will be unavailable for the rest of this process's lifetime (warm-up is not retried).");
        }
    }
}