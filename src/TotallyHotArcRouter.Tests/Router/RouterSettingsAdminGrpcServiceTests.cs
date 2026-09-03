using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="RouterSettingsAdminGrpcService"/> (docs/router/self-organizing-classification-plan.md
/// Phase T6): reading the currently effective values, persisting a valid update and reflecting it
/// immediately via the live options monitor, and rejecting an out-of-range capacity with a structured
/// error rather than silently clamping it.
/// </summary>
public sealed class RouterSettingsAdminGrpcServiceTests
{
    private static ServerCallContext CreateContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: TestContext.Current.CancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task GetRouterSettings_ReportsTheCurrentlyEffectiveValues()
    {
        var monitor = new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
        {
            EnableAdaptiveRouting = true,
            EmbeddingMemoryCapacity = 12_345
        });
        var service = CreateService(monitor: monitor);

        var response = await service.GetRouterSettings(request: new Contract.GetRouterSettingsRequest(),
            context: CreateContext());

        response.AdaptiveRoutingEnabled.Should().BeTrue();
        response.EmbeddingMemoryCapacity.Should().Be(12_345);
    }

    [Fact]
    public async Task UpdateRouterSettings_ValidRequest_PersistsAndTriggersReload()
    {
        var store = CreateStore();
        var reloadToken = new RouterSettingsReloadToken();
        var monitor = new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions());
        var service = CreateService(store: store, reloadToken: reloadToken, monitor: monitor);
        var triggered = false;
        using var subscription =
            reloadToken.GetChangeToken().RegisterChangeCallback(callback: _ => triggered = true, null);

        var response = await service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            { AdaptiveRoutingEnabled = true, EmbeddingMemoryCapacity = 8_000 },
            context: CreateContext());

        store.TryGetBool(key: RouterSettingsStore.AdaptiveRoutingEnabledKey, value: out var storedEnabled).Should()
            .BeTrue();
        storedEnabled.Should().BeTrue();
        store.TryGetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: out var storedCapacity).Should()
            .BeTrue();
        storedCapacity.Should().Be(8_000);
        triggered.Should().BeTrue("a successful save must trigger the live-reload change token");

        // The response reports whatever the options monitor currently reflects, not the raw request - in
        // this test double that's still the pre-update RoutingOptions() since nothing re-runs the
        // configure pipeline on its own, mirroring how the response is a re-read rather than an echo.
        response.Should().NotBeNull();
    }

    [Theory]
    [InlineData(499)]
    [InlineData(50_001)]
    public async Task UpdateRouterSettings_CapacityOutOfRange_RejectsWithInvalidArgumentRatherThanClamping(
        int outOfRangeCapacity)
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        var act = () => service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            { AdaptiveRoutingEnabled = true, EmbeddingMemoryCapacity = outOfRangeCapacity },
            context: CreateContext());

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        // Rejected, not clamped: nothing should have been written.
        store.TryGetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(50_000)]
    public async Task UpdateRouterSettings_CapacityAtInclusiveBounds_Accepted(int boundaryCapacity)
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        await service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            { AdaptiveRoutingEnabled = false, EmbeddingMemoryCapacity = boundaryCapacity },
            context: CreateContext());

        store.TryGetInt(key: RouterSettingsStore.EmbeddingMemoryCapacityKey, value: out var storedCapacity).Should()
            .BeTrue();
        storedCapacity.Should().Be(boundaryCapacity);
    }

    [Fact]
    public async Task GetRouterSettings_ReportsTheJudgeSettingsAndTheEligibleBackboneList()
    {
        var service = CreateService(
            judgeMonitor: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            { Enabled = true, ModelName = "free-judge" }));

        var response = await service.GetRouterSettings(request: new Contract.GetRouterSettingsRequest(),
            context: CreateContext());

        response.JudgeEnabled.Should().BeTrue();
        response.JudgeModelName.Should().Be("free-judge");
        response.EligibleJudgeModels.Should().Equal("free-judge");
    }

    [Fact]
    public async Task UpdateRouterSettings_PersistsTheJudgeSettings()
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        await service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            {
                AdaptiveRoutingEnabled = false,
                EmbeddingMemoryCapacity = 20_000,
                JudgeEnabled = true,
                JudgeModelName = "free-judge"
            },
            context: CreateContext());

        store.TryGetBool(key: RouterSettingsStore.JudgeEnabledKey, value: out var enabled).Should().BeTrue();
        enabled.Should().BeTrue();
        store.TryGetString(key: RouterSettingsStore.JudgeModelNameKey, value: out var modelName).Should().BeTrue();
        modelName.Should().Be("free-judge");
    }

    /// <summary>
    /// Empty is the explicit "automatic" choice and must always be accepted - including when no free
    /// provider exists at all, so the operator can still switch the judge on ahead of configuring one.
    /// </summary>
    [Fact]
    public async Task UpdateRouterSettings_EmptyJudgeModelName_IsAcceptedAsAutomatic()
    {
        var store = CreateStore();
        var service = CreateService(store: store, judgeModelSelector: CreateJudgeModelSelector(freeModelName: null));

        await service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            { EmbeddingMemoryCapacity = 20_000, JudgeModelName = string.Empty },
            context: CreateContext());

        store.TryGetString(key: RouterSettingsStore.JudgeModelNameKey, value: out var modelName).Should().BeTrue();
        modelName.Should().BeEmpty();
    }

    /// <summary>
    /// Rejected rather than coerced: silently saving a model the selector would not call leaves the window
    /// displaying a setting that is not actually in force.
    /// </summary>
    [Fact]
    public async Task UpdateRouterSettings_IneligibleJudgeModel_IsRejectedAndNothingIsPersisted()
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        var act = () => service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            { EmbeddingMemoryCapacity = 20_000, JudgeModelName = "not-a-free-model" },
            context: CreateContext());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        store.TryGetString(key: RouterSettingsStore.JudgeModelNameKey, value: out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetRouterSettings_ReportsTheTranscriptCaptureSetting()
    {
        var service = CreateService(
            transcriptMonitor: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false }));

        var response = await service.GetRouterSettings(request: new Contract.GetRouterSettingsRequest(),
            context: CreateContext());

        response.TranscriptCaptureEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateRouterSettings_PersistsTheTranscriptCaptureSetting()
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        await service.UpdateRouterSettings(
            request: new Contract.UpdateRouterSettingsRequest
            { EmbeddingMemoryCapacity = 20_000, TranscriptCaptureEnabled = true },
            context: CreateContext());

        store.TryGetBool(key: RouterSettingsStore.TranscriptCaptureEnabledKey, value: out var enabled).Should()
            .BeTrue();
        enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ClearTranscripts_DelegatesToTheStoreAndReportsTheDeletedCount()
    {
        var transcriptStore = new FakeTranscriptStore(rowsToDelete: 7);
        var service = CreateService(transcriptStore: transcriptStore);

        var response =
            await service.ClearTranscripts(request: new Contract.ClearTranscriptsRequest(), context: CreateContext());

        response.RowsDeleted.Should().Be(7);
        transcriptStore.DeleteAllCallCount.Should().Be(1);
    }

    [Fact]
    public void Constructor_ThrowsOnNullStore()
    {
        var act = () => new RouterSettingsAdminGrpcService(
            store: null!,
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            judgeOptionsMonitor: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            judgeModelSelector: CreateJudgeModelSelector(),
            reloadToken: new RouterSettingsReloadToken(),
            logger: NullLogger<RouterSettingsAdminGrpcService>.Instance,
            transcriptOptionsMonitor: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions()),
            transcriptStore: new FakeTranscriptStore());
        act.Should().Throw<ArgumentNullException>();
    }

    private static RouterSettingsAdminGrpcService CreateService(
        RouterSettingsStore? store = null,
        RouterSettingsReloadToken? reloadToken = null,
        StaticOptionsMonitor<RoutingOptions>? monitor = null,
        StaticOptionsMonitor<JudgeOptions>? judgeMonitor = null,
        JudgeModelSelector? judgeModelSelector = null,
        StaticOptionsMonitor<TranscriptOptions>? transcriptMonitor = null,
        ITranscriptStore? transcriptStore = null)
    {
        return new RouterSettingsAdminGrpcService(
            store: store ?? CreateStore(),
            optionsMonitor: monitor ?? new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            judgeOptionsMonitor: judgeMonitor ?? new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            judgeModelSelector: judgeModelSelector ?? CreateJudgeModelSelector(),
            reloadToken: reloadToken ?? new RouterSettingsReloadToken(),
            logger: NullLogger<RouterSettingsAdminGrpcService>.Instance,
            transcriptOptionsMonitor: transcriptMonitor ??
                                      new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions()),
            transcriptStore: transcriptStore ?? new FakeTranscriptStore());
    }

    /// <summary>
    /// A selector over one free model, so the judge-model validation has something eligible to accept.
    /// Pass <paramref name="freeModelName"/> as null for the no-free-provider case.
    /// </summary>
    private static JudgeModelSelector CreateJudgeModelSelector(string? freeModelName = "free-judge")
    {
        return new JudgeModelSelector(
            routeResolver: freeModelName is null
                ? ModelRouteResolverTestFactory.Create(
                    modelName: "paid-only",
                    providerModelId: "paid-only",
                    baseUrl: "https://api.openai.com",
                    isFree: false)
                : ModelRouteResolverTestFactory.Create(
                    modelName: freeModelName,
                    providerModelId: freeModelName,
                    baseUrl: "http://localhost:1234/v1",
                    isFree: true),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<JudgeModelSelector>.Instance);
    }

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(path1: tempDirectory, path2: "router_embedding_memory.db");
        var database =
            new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database: database, logger: NullLogger<RouterSettingsStore>.Instance);
    }

    /// <summary>
    /// A minimal <see cref="ITranscriptStore"/> test double covering only
    /// <see cref="ITranscriptStore.DeleteAllAsync"/>.
    /// </summary>
    private sealed class FakeTranscriptStore(int rowsToDelete = 0) : ITranscriptStore
    {
        public int DeleteAllCallCount { get; private set; }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            DeleteAllCallCount++;
            return Task.FromResult(rowsToDelete);
        }

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}