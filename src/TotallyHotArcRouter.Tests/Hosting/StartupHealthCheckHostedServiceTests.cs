using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

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
        var sourceRepository = temp.CreateSourceRepository();
        var ledger = temp.CreateUsageLedger();

        var retentionDays = 30;
        var now = DateTimeOffset.UtcNow;
        await ledger.RecordAsync(entry: MakeEntry(requestId: "old", occurredAtUtc: now.AddDays(-(retentionDays + 10))),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(requestId: "recent", occurredAtUtc: now.AddDays(-1)),
            cancellationToken: TestContext.Current.CancellationToken);

        var registry = Mock.Of<IPriceSourceRegistry>(r => r.EnabledClients == Array.Empty<IPriceSourceClient>());
        var ingestionService = new PriceCatalogIngestionService(
            registry: registry, repository: repository, sourceRepository: sourceRepository,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            logger: NullLogger<PriceCatalogIngestionService>.Instance);

        var transcriptDb = CreateTranscriptDatabase(temp);
        var transcriptStore = new SqliteTranscriptStore(
            database: transcriptDb, options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions()));
        var service = new StartupHealthCheckHostedService(
            logger: NullLogger<StartupHealthCheckHostedService>.Instance,
            database: temp.Database,
            repository: sourceRepository,
            ingestionService: ingestionService,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            budgetStore: temp.CreateBudgetStore(),
            toolCallCapabilityStore: temp.CreateToolCallCapabilityStore(),
            usageLedger: ledger,
            rollupStore: temp.CreateRollupStore(),
            storageOptions: Options.Create(new StorageOptions { UsageLedgerRetentionDays = retentionDays }),
            routerMemoryDatabase: CreateRouterMemoryDatabase(temp),
            routerMemory: new RouterMemory(),
            embeddingMemory: CreateEmbeddingMemory(temp),
            benchmarkDatabase: CreateBenchmarkDatabase(temp),
            benchmarkStatusService: CreateBenchmarkStatusService(temp),
            transcriptDatabase: transcriptDb,
            transcriptStore: transcriptStore,
            transcriptOptions: Options.Create(new TranscriptOptions()));

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: ledger.GetMaxTurnNumber("does-not-exist")); // sanity: ledger still usable
        Assert.Equal(0, actual: CountRows(temp: temp, sessionSuffix: "old"));
        Assert.Equal(1, actual: CountRows(temp: temp, sessionSuffix: "recent"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartAsync_NonPositiveRetentionDays_SkipsSweepInsteadOfDeletingEverything(int retentionDays)
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        var ledger = temp.CreateUsageLedger();

        var now = DateTimeOffset.UtcNow;
        await ledger.RecordAsync(entry: MakeEntry(requestId: "recent", occurredAtUtc: now.AddDays(-1)),
            cancellationToken: TestContext.Current.CancellationToken);

        var registry = Mock.Of<IPriceSourceRegistry>(r => r.EnabledClients == Array.Empty<IPriceSourceClient>());
        var ingestionService = new PriceCatalogIngestionService(
            registry: registry, repository: repository, sourceRepository: sourceRepository,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            logger: NullLogger<PriceCatalogIngestionService>.Instance);

        var transcriptDb = CreateTranscriptDatabase(temp);
        var transcriptStore = new SqliteTranscriptStore(
            database: transcriptDb, options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions()));
        var service = new StartupHealthCheckHostedService(
            logger: NullLogger<StartupHealthCheckHostedService>.Instance,
            database: temp.Database,
            repository: sourceRepository,
            ingestionService: ingestionService,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            budgetStore: temp.CreateBudgetStore(),
            toolCallCapabilityStore: temp.CreateToolCallCapabilityStore(),
            usageLedger: ledger,
            rollupStore: temp.CreateRollupStore(),
            storageOptions: Options.Create(new StorageOptions { UsageLedgerRetentionDays = retentionDays }),
            routerMemoryDatabase: CreateRouterMemoryDatabase(temp),
            routerMemory: new RouterMemory(),
            embeddingMemory: CreateEmbeddingMemory(temp),
            benchmarkDatabase: CreateBenchmarkDatabase(temp),
            benchmarkStatusService: CreateBenchmarkStatusService(temp),
            transcriptDatabase: transcriptDb,
            transcriptStore: transcriptStore,
            transcriptOptions: Options.Create(new TranscriptOptions()));

        await service.StartAsync(TestContext.Current.CancellationToken);

        // A 0/negative retention window must never be treated as "cutoff = now", which would wipe out
        // every row instead of leaving the ledger untouched.
        Assert.Equal(1, actual: CountRows(temp: temp, sessionSuffix: "recent"));
    }

    [Fact]
    public async Task StartAsync_EmbeddingClientConfigured_WarmsUpAndMarksStateWarm()
    {
        using var temp = new TempDatabase();
        var service = CreateMinimalService(temp: temp,
            embeddingClient: new FakeEmbeddingClient(succeed: true), warmupState: out var warmupState);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.EmbeddingWarmupTask!;

        Assert.True(warmupState!.IsWarm);
    }

    [Fact]
    public async Task StartAsync_EmbeddingClientThrows_LeavesStateNotWarm_AndDoesNotThrow()
    {
        using var temp = new TempDatabase();
        var service = CreateMinimalService(temp: temp,
            embeddingClient: new FakeEmbeddingClient(succeed: false), warmupState: out var warmupState);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.EmbeddingWarmupTask!;

        Assert.False(warmupState!.IsWarm);
    }

    [Fact]
    public async Task StartAsync_NoEmbeddingClientConfigured_DoesNotThrow()
    {
        using var temp = new TempDatabase();
        var service = CreateMinimalService(temp: temp, null, warmupState: out var warmupState);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(warmupState);
        Assert.Null(service.EmbeddingWarmupTask);
    }

    /// <summary>
    /// Builds a service with every dependency minimally stubbed, for tests that only care about the
    /// embedding warm-up step and would otherwise have to repeat every other constructor argument.
    /// </summary>
    private static StartupHealthCheckHostedService CreateMinimalService(
        TempDatabase temp,
        IEmbeddingClient? embeddingClient,
        out EmbeddingWarmupState? warmupState)
    {
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        var ledger = temp.CreateUsageLedger();
        var registry = Mock.Of<IPriceSourceRegistry>(r => r.EnabledClients == Array.Empty<IPriceSourceClient>());
        var ingestionService = new PriceCatalogIngestionService(
            registry: registry, repository: repository, sourceRepository: sourceRepository,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            logger: NullLogger<PriceCatalogIngestionService>.Instance);
        warmupState = embeddingClient is null ? null : new EmbeddingWarmupState();

        var transcriptDb = CreateTranscriptDatabase(temp);
        var transcriptStore = new SqliteTranscriptStore(
            database: transcriptDb, options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions()));

        return new StartupHealthCheckHostedService(
            logger: NullLogger<StartupHealthCheckHostedService>.Instance,
            database: temp.Database,
            repository: sourceRepository,
            ingestionService: ingestionService,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            budgetStore: temp.CreateBudgetStore(),
            toolCallCapabilityStore: temp.CreateToolCallCapabilityStore(),
            usageLedger: ledger,
            rollupStore: temp.CreateRollupStore(),
            storageOptions: Options.Create(new StorageOptions()),
            routerMemoryDatabase: CreateRouterMemoryDatabase(temp),
            routerMemory: new RouterMemory(),
            embeddingMemory: CreateEmbeddingMemory(temp),
            benchmarkDatabase: CreateBenchmarkDatabase(temp),
            benchmarkStatusService: CreateBenchmarkStatusService(temp),
            transcriptDatabase: transcriptDb,
            transcriptStore: transcriptStore,
            transcriptOptions: Options.Create(new TranscriptOptions()),
            embeddingClient: embeddingClient,
            embeddingWarmupState: warmupState);
    }

    private static UsageLedgerEntry MakeEntry(string requestId, DateTimeOffset occurredAtUtc)
    {
        return new UsageLedgerEntry(
            SessionId: "sess-" + requestId,
            1,
            Provider: "openai",
            RequestedModel: "gpt-4o",
            ResolvedModel: "gpt-4o",
            10,
            5,
            null,
            null,
            0.01m,
            CostConfidence: CostConfidence.Unknown,
            OccurredAtUtc: occurredAtUtc,
            RequestId: requestId);
    }

    private static RouterMemoryDatabase CreateRouterMemoryDatabase(TempDatabase temp)
    {
        var directory = Path.GetDirectoryName(temp.DatabasePath)!;
        var dbPath = Path.Combine(path1: directory, path2: "router_embedding_memory.db");
        return new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
    }

    private static EmbeddingMemory CreateEmbeddingMemory(TempDatabase temp)
    {
        var database = CreateRouterMemoryDatabase(temp);
        var store = new SqliteMemoryEntryStore(database);
        return new EmbeddingMemory(store: store,
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            embeddingClient: new StubEmbeddingClient(), logger: NullLogger<EmbeddingMemory>.Instance);
    }

    private static BenchmarkDatabase CreateBenchmarkDatabase(TempDatabase temp)
    {
        var directory = Path.GetDirectoryName(temp.DatabasePath)!;
        var dbPath = Path.Combine(path1: directory, path2: "coderouterbench.db");
        return new BenchmarkDatabase(Options.Create(new StorageOptions { BenchmarkDatabasePath = dbPath }));
    }

    private static TranscriptDatabase CreateTranscriptDatabase(TempDatabase temp)
    {
        var directory = Path.GetDirectoryName(temp.DatabasePath)!;
        var dbPath = Path.Combine(path1: directory, path2: "transcripts.db");
        return new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = dbPath }));
    }

    // The probe's HttpClient always fails fast (no real network I/O), so RecheckAsync resolves to
    // CheckFailed rather than hanging or reaching out to Hugging Face during this unrelated retention test.
    private static BenchmarkDataStatusService CreateBenchmarkStatusService(TempDatabase temp)
    {
        var probe = new BenchmarkChecksumProbe(
            httpClientFactory: new FakeHttpClientFactory(FakeHttpMessageHandler.AlwaysFails()),
            logger: NullLogger<BenchmarkChecksumProbe>.Instance);
        var ledger = new BenchmarkFileLedger(CreateBenchmarkDatabase(temp));
        return new BenchmarkDataStatusService(
            probe: probe, ledger: ledger, options: Options.Create(new BenchmarkSyncOptions()),
            logger: NullLogger<BenchmarkDataStatusService>.Instance);
    }

    private static int CountRows(TempDatabase temp, string sessionSuffix)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_ledger WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue(parameterName: "$sessionId", value: "sess-" + sessionSuffix);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed class FakeEmbeddingClient(bool succeed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return succeed
                ? Task.FromResult(new EmbeddingResult(Vector: [1f], 1))
                : throw new InvalidOperationException("embedding backend unavailable");
        }
    }
}