using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="StartupHealthCheckHostedService"/>'s usage-ledger retention sweep
/// (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2, §5.2's retention requirement). The
/// pre-existing pricing health checks are covered elsewhere by integration-level tests; this focuses on
/// the new retention boundary.
/// </summary>
public class StartupHealthCheckHostedServiceTests
{
    [Fact]
    public async Task StartAsync_DeletesUsageLedgerRowsOlderThanRetentionWindow()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var ledger = temp.CreateUsageLedger();

        var retentionDays = 30;
        var now = DateTimeOffset.UtcNow;
        await ledger.RecordAsync(MakeEntry("old", now.AddDays(-(retentionDays + 10))), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry("recent", now.AddDays(-1)), TestContext.Current.CancellationToken);

        var registry = Mock.Of<IPriceSourceRegistry>(r => r.EnabledClients == Array.Empty<IPriceSourceClient>());
        var ingestionService = new PriceCatalogIngestionService(
            registry, repository, temp.CreateToggleStore(repository), NullLogger<PriceCatalogIngestionService>.Instance);

        var service = new StartupHealthCheckHostedService(
            NullLogger<StartupHealthCheckHostedService>.Instance,
            temp.Database,
            repository,
            ingestionService,
            temp.CreateToggleStore(repository),
            temp.CreateBudgetStore(repository),
            temp.CreateToolCallCapabilityStore(),
            ledger,
            temp.CreateRollupStore(),
            Options.Create(new StorageOptions { UsageLedgerRetentionDays = retentionDays }),
            CreateRouterMemoryDatabase(temp),
            CreateEmbeddingMemory(temp));

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, ledger.GetMaxTurnNumber("does-not-exist")); // sanity: ledger still usable
        Assert.Equal(0, CountRows(temp, "old"));
        Assert.Equal(1, CountRows(temp, "recent"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartAsync_NonPositiveRetentionDays_SkipsSweepInsteadOfDeletingEverything(int retentionDays)
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var ledger = temp.CreateUsageLedger();

        var now = DateTimeOffset.UtcNow;
        await ledger.RecordAsync(MakeEntry("recent", now.AddDays(-1)), TestContext.Current.CancellationToken);

        var registry = Mock.Of<IPriceSourceRegistry>(r => r.EnabledClients == Array.Empty<IPriceSourceClient>());
        var ingestionService = new PriceCatalogIngestionService(
            registry, repository, temp.CreateToggleStore(repository), NullLogger<PriceCatalogIngestionService>.Instance);

        var service = new StartupHealthCheckHostedService(
            NullLogger<StartupHealthCheckHostedService>.Instance,
            temp.Database,
            repository,
            ingestionService,
            temp.CreateToggleStore(repository),
            temp.CreateBudgetStore(repository),
            temp.CreateToolCallCapabilityStore(),
            ledger,
            temp.CreateRollupStore(),
            Options.Create(new StorageOptions { UsageLedgerRetentionDays = retentionDays }),
            CreateRouterMemoryDatabase(temp),
            CreateEmbeddingMemory(temp));

        await service.StartAsync(TestContext.Current.CancellationToken);

        // A 0/negative retention window must never be treated as "cutoff = now", which would wipe out
        // every row instead of leaving the ledger untouched.
        Assert.Equal(1, CountRows(temp, "recent"));
    }

    private static UsageLedgerEntry MakeEntry(string requestId, DateTimeOffset occurredAtUtc) =>
        new(
            SessionId: "sess-" + requestId,
            TurnNumber: 1,
            Provider: "openai",
            RequestedModel: "gpt-4o",
            ResolvedModel: "gpt-4o",
            PromptTokens: 10,
            CompletionTokens: 5,
            CacheCreationTokens: null,
            CacheReadTokens: null,
            EstimatedCostUsd: 0.01m,
            CostConfidence: CostConfidence.Unknown,
            OccurredAtUtc: occurredAtUtc,
            RequestId: requestId);

    private static RouterMemoryDatabase CreateRouterMemoryDatabase(TempDatabase temp)
    {
        var directory = Path.GetDirectoryName(temp.Path_)!;
        var dbPath = Path.Combine(directory, "router_embedding_memory.db");
        return new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
    }

    private static EmbeddingMemory CreateEmbeddingMemory(TempDatabase temp)
    {
        var database = CreateRouterMemoryDatabase(temp);
        var store = new SqliteMemoryEntryStore(database);
        return new EmbeddingMemory(store, Options.Create(new RoutingOptions()), NullLogger<EmbeddingMemory>.Instance);
    }

    private static int CountRows(TempDatabase temp, string sessionSuffix)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_ledger WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", "sess-" + sessionSuffix);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
