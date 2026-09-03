using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// A throwaway <see cref="PriceCatalogDatabase"/> backed by a unique temp file, cleaned up on dispose
/// (including SQLite's WAL/SHM sidecar files). Keeps each test isolated from every other.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Path_ = Path.Combine(path1: directory, path2: "agent_telemetry.db");
        Database = new PriceCatalogDatabase(Options.Create(new StorageOptions { DatabasePath = Path_ }));
    }

    public string Path_ { get; }

    public PriceCatalogDatabase Database { get; }

    public void Dispose()
    {
        // ClearPool (scoped to this test's own connection string), not the process-global ClearAllPools:
        // under xUnit's parallel test execution, ClearAllPools can tear down a pooled native sqlite3
        // handle out from under a completely different test's in-flight query, surfacing as a spurious
        // ObjectDisposedException there. Guarded on the file already existing - a test that never called
        // EnsureCreated() never opened a pooled connection (and its directory may not even exist), so
        // there's nothing to clear.
        if (File.Exists(Path_))
            try
            {
                using var connection = Database.OpenConnection();
                SqliteConnection.ClearPool(connection);
            }
            catch (SqliteException)
            {
                // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
            }

        var directory = Path.GetDirectoryName(Path_);
        try
        {
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(path: directory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }

    /// <summary>Creates the schema (seeding the known sources) and returns a price repository over it.</summary>
    public PriceRepository CreateRepository()
    {
        Database.EnsureCreated();
        return new PriceRepository(Database);
    }

    /// <summary>Creates the schema (seeding the known sources) and returns a source-toggle repository over it.</summary>
    public PriceSourceRepository CreateSourceRepository()
    {
        Database.EnsureCreated();
        return new PriceSourceRepository(Database);
    }

    /// <summary>Creates the schema and returns a provider-budget repository over it.</summary>
    public ProviderBudgetRepository CreateBudgetRepository()
    {
        Database.EnsureCreated();
        return new ProviderBudgetRepository(Database);
    }

    /// <summary>Creates the schema and returns a provider-spend repository over it.</summary>
    public ProviderSpendRepository CreateSpendRepository()
    {
        Database.EnsureCreated();
        return new ProviderSpendRepository(Database);
    }

    /// <summary>Creates the schema and returns a rate-limit repository over it.</summary>
    public RateLimitRepository CreateRateLimitRepository()
    {
        Database.EnsureCreated();
        return new RateLimitRepository(Database);
    }

    /// <summary>Creates the schema and returns a reported-usage repository over it.</summary>
    public ReportedUsageRepository CreateReportedUsageRepository()
    {
        Database.EnsureCreated();
        return new ReportedUsageRepository(Database);
    }

    /// <summary>
    /// Inserts an extra <c>aggregator_sources</c> row directly, standing in for a second source with a
    /// client. Only <c>litellm</c> is seeded today, so multi-source behavior (a toggle cancelling one source
    /// while another finishes) has nothing to exercise otherwise. Call before <see cref="CreateToggleStore"/>
    /// so the store's reload sees it.
    /// </summary>
    public void SeedExtraSource(string name, bool enabled = true, int priorityScore = 0)
    {
        Database.EnsureCreated();

        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        // DO UPDATE, unlike the production seed's DO NOTHING: this helper's whole job is to force a state,
        // and EnsureCreated has already seeded litellm by the time it runs.
        command.CommandText = """
                              INSERT INTO aggregator_sources (source_name, priority_score, enabled)
                              VALUES ($name, $priority, $enabled)
                              ON CONFLICT(source_name) DO UPDATE SET
                                  enabled        = excluded.enabled,
                                  priority_score = excluded.priority_score;
                              """;
        command.Parameters.AddWithValue(parameterName: "$name", value: name);
        command.Parameters.AddWithValue(parameterName: "$priority", value: priorityScore);
        command.Parameters.AddWithValue(parameterName: "$enabled", value: enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the schema and returns a loaded toggle store. Mirrors what
    /// <see cref="TotallyHot.ArcRouter.Hosting.StartupHealthCheckHostedService"/> does at startup: EnsureCreated
    /// first, then Reload - a store that never reloads reports every source disabled.
    /// </summary>
    public PriceSourceToggleStore CreateToggleStore(PriceSourceRepository? repository = null)
    {
        var store = new PriceSourceToggleStore(
            repository: repository ?? CreateSourceRepository(),
            logger: NullLogger<PriceSourceToggleStore>.Instance);
        store.Reload();
        return store;
    }

    /// <summary>
    /// Creates the schema and returns a loaded <see cref="ProviderBudgetStore"/>, mirroring the startup path
    /// (EnsureCreated, then Reload). A store that never reloads reports every provider unbudgeted.
    /// </summary>
    public ProviderBudgetStore CreateBudgetStore(
        ProviderBudgetRepository? budgetRepository = null,
        ProviderSpendRepository? spendRepository = null)
    {
        var store = new ProviderBudgetStore(
            budgetRepository: budgetRepository ?? CreateBudgetRepository(),
            spendRepository: spendRepository ?? CreateSpendRepository(),
            logger: NullLogger<ProviderBudgetStore>.Instance);
        store.Reload();
        return store;
    }

    /// <summary>
    /// Creates the schema and returns a loaded <see cref="ToolCallCapabilityStore"/>, mirroring the startup
    /// path (EnsureCreated, then Reload). A store that never reloads reports every model unclassified.
    /// </summary>
    public ToolCallCapabilityStore CreateToolCallCapabilityStore()
    {
        Database.EnsureCreated();
        var store = new ToolCallCapabilityStore(
            repository: new ToolCallCapabilityRepository(Database),
            logger: NullLogger<ToolCallCapabilityStore>.Instance);
        store.Reload();
        return store;
    }

    /// <summary>Creates the schema and returns a <see cref="UsageLedger"/> over it, optionally wired to a rollup store.</summary>
    public UsageLedger CreateUsageLedger(IUsageRollupStore? rollupStore = null)
    {
        Database.EnsureCreated();
        return new UsageLedger(database: Database, rollupStore: rollupStore, logger: NullLogger<UsageLedger>.Instance);
    }

    /// <summary>Creates the schema and returns a <see cref="UsageRollupStore"/> over it.</summary>
    public UsageRollupStore CreateRollupStore(string rollupTimezone = "UTC")
    {
        Database.EnsureCreated();
        return new UsageRollupStore(
            database: Database,
            storageOptions: Options.Create(new StorageOptions
            { DatabasePath = Path_, RollupTimezone = rollupTimezone }),
            logger: NullLogger<UsageRollupStore>.Instance);
    }

    /// <summary>Creates the schema and returns a <see cref="ModelAliasOverrideStore"/> over it.</summary>
    public ModelAliasOverrideStore CreateOverrideStore()
    {
        Database.EnsureCreated();
        return new ModelAliasOverrideStore(Database);
    }

    /// <summary>Creates the schema and returns a <see cref="ProviderCostReconciliationStore"/> over it.</summary>
    public ProviderCostReconciliationStore CreateCostReconciliationStore()
    {
        Database.EnsureCreated();
        return new ProviderCostReconciliationStore(Database);
    }
}