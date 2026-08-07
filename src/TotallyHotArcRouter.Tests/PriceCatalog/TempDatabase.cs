using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// A throwaway <see cref="PriceCatalogDatabase"/> backed by a unique temp file, cleaned up on dispose
/// (including SQLite's WAL/SHM sidecar files). Keeps each test isolated from every other.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Path_ = Path.Combine(directory, "agent_telemetry.db");
        Database = new PriceCatalogDatabase(Options.Create(new StorageOptions { DatabasePath = Path_ }));
    }

    public string Path_ { get; }

    public PriceCatalogDatabase Database { get; }

    /// <summary>Creates the schema (seeding the known sources) and returns a repository over it.</summary>
    public PriceCatalogRepository CreateRepository()
    {
        Database.EnsureCreated();
        return new PriceCatalogRepository(Database);
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
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$priority", priorityScore);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the schema and returns a loaded toggle store. Mirrors what
    /// <see cref="TotallyHot.ArcRouter.Hosting.StartupHealthCheckHostedService"/> does at startup: EnsureCreated
    /// first, then Reload - a store that never reloads reports every source disabled.
    /// </summary>
    public PriceSourceToggleStore CreateToggleStore(PriceCatalogRepository? repository = null)
    {
        var store = new PriceSourceToggleStore(
            repository ?? CreateRepository(),
            NullLogger<PriceSourceToggleStore>.Instance);
        store.Reload();
        return store;
    }

    /// <summary>
    /// Creates the schema and returns a loaded <see cref="ProviderBudgetStore"/>, mirroring the startup path
    /// (EnsureCreated, then Reload). A store that never reloads reports every provider unbudgeted.
    /// </summary>
    public ProviderBudgetStore CreateBudgetStore(PriceCatalogRepository? repository = null)
    {
        var store = new ProviderBudgetStore(
            repository ?? CreateRepository(),
            NullLogger<ProviderBudgetStore>.Instance);
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
            new ToolCallCapabilityRepository(Database),
            NullLogger<ToolCallCapabilityStore>.Instance);
        store.Reload();
        return store;
    }

    /// <summary>Creates the schema and returns a <see cref="UsageLedger"/> over it.</summary>
    public UsageLedger CreateUsageLedger()
    {
        Database.EnsureCreated();
        return new UsageLedger(Database, NullLogger<UsageLedger>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var directory = System.IO.Path.GetDirectoryName(Path_);
        try
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }
}

