using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    // D1's routing floor, reused here only to describe the check-4 condition in the log message.
    private static readonly TimeSpan FreshnessFloor = TimeSpan.FromHours(24);

    private readonly ILogger<StartupHealthCheckHostedService> _logger;
    private readonly PriceCatalogDatabase _database;
    private readonly PriceCatalogRepository _repository;
    private readonly PriceCatalogIngestionService _ingestionService;
    private readonly PriceSourceToggleStore _toggleStore;
    private readonly ProviderBudgetStore _budgetStore;
    private readonly ToolCallCapabilityStore _toolCallCapabilityStore;
    private readonly IUsageLedger _usageLedger;
    private readonly IUsageRollupStore _rollupStore;
    private readonly StorageOptions _storageOptions;
    private readonly Router.RouterMemoryDatabase _routerMemoryDatabase;
    private readonly Router.EmbeddingMemory _embeddingMemory;
    private readonly CodeRouterBench.BenchmarkDatabase _benchmarkDatabase;
    private readonly CodeRouterBench.BenchmarkDataStatusService _benchmarkStatusService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupHealthCheckHostedService"/> class.
    /// </summary>
    public StartupHealthCheckHostedService(
        ILogger<StartupHealthCheckHostedService> logger,
        PriceCatalogDatabase database,
        PriceCatalogRepository repository,
        PriceCatalogIngestionService ingestionService,
        PriceSourceToggleStore toggleStore,
        ProviderBudgetStore budgetStore,
        ToolCallCapabilityStore toolCallCapabilityStore,
        IUsageLedger usageLedger,
        IUsageRollupStore rollupStore,
        IOptions<StorageOptions> storageOptions,
        Router.RouterMemoryDatabase routerMemoryDatabase,
        Router.EmbeddingMemory embeddingMemory,
        CodeRouterBench.BenchmarkDatabase benchmarkDatabase,
        CodeRouterBench.BenchmarkDataStatusService benchmarkStatusService)
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
        ArgumentNullException.ThrowIfNull(embeddingMemory);
        ArgumentNullException.ThrowIfNull(benchmarkDatabase);
        ArgumentNullException.ThrowIfNull(benchmarkStatusService);

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
        _embeddingMemory = embeddingMemory;
        _benchmarkDatabase = benchmarkDatabase;
        _benchmarkStatusService = benchmarkStatusService;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Startup pricing health checks are running.");

        // Check 1: ensure the SQLite database exists, creating it (and its directory) if absent. This also
        // applies additive column migrations and seeds a row for every source that has a client, so it must
        // precede the toggle store's first read below.
        var alreadyExisted = _database.EnsureCreated();
        if (alreadyExisted)
        {
            _logger.LogInformation("Found existing pricing database at {Path}.", _database.DatabasePath);
        }
        else
        {
            _logger.LogInformation("Created new pricing database at {Path}.", _database.DatabasePath);
        }

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
        {
            _logger.LogWarning(
                "No pricing data sources are enabled; cost estimates will be unavailable for all paid models.");
        }

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
        {
            // Only reached when no source ran (check 2 found none enabled); a cycle that ran already
            // logged this via RunCycleAsync.
            _logger.LogError(
                "No pricing data is available: no manual prices are configured and all fetched price data is missing or older than {FreshnessHours} hours.",
                FreshnessFloor.TotalHours);
        }

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
                    "Skipping the usage-ledger retention sweep: Storage:UsageLedgerRetentionDays is {RetentionDays}, must be positive.",
                    _storageOptions.UsageLedgerRetentionDays);
            }
            else
            {
                var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_storageOptions.UsageLedgerRetentionDays);
                var deleted = _usageLedger.DeleteOlderThan(cutoff);
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Usage-ledger retention sweep deleted {DeletedRows} row(s) older than {RetentionDays} days.",
                        deleted,
                        _storageOptions.UsageLedgerRetentionDays);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Usage-ledger retention sweep failed; continuing startup.");
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
            {
                _logger.LogInformation("Usage-rollup backfill applied {EntryCount} ledger entry/entries.", rolledUp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Usage-rollup backfill failed; continuing startup.");
        }

        // Task-embedding-keyed memory (PLAN.md Phase J): ensure its own SQLite schema exists and load
        // the working set, best-effort and log-only like every check above - Phase J's store is not yet
        // on the routing decision path (Phase L wires the Orchestrator's memory_kNN voter to it), so a
        // failure here must not block the proxy from binding its port.
        try
        {
            _routerMemoryDatabase.EnsureCreated();
            await _embeddingMemory.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding memory initialization failed; continuing startup.");
        }

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
            _logger.LogWarning(ex, "CodeRouterBench corpus initialization failed; continuing startup.");
        }

        _logger.LogInformation("Startup pricing health checks complete.");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

