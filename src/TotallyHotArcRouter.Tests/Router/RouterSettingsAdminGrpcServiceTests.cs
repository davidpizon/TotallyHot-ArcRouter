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
    private static ServerCallContext CreateContext() =>
        TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: TestContext.Current.CancellationToken,
            peer: "test-peer",
            authContext: null!,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });

    [Fact]
    public async Task GetRouterSettings_ReportsTheCurrentlyEffectiveValues()
    {
        var monitor = new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
        {
            EnableAdaptiveRouting = true,
            EmbeddingMemoryCapacity = 12_345,
        });
        var service = CreateService(monitor: monitor);

        var response = await service.GetRouterSettings(new Contract.GetRouterSettingsRequest(), CreateContext());

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
        using var subscription = reloadToken.GetChangeToken().RegisterChangeCallback(_ => triggered = true, null);

        var response = await service.UpdateRouterSettings(
            new Contract.UpdateRouterSettingsRequest { AdaptiveRoutingEnabled = true, EmbeddingMemoryCapacity = 8_000 },
            CreateContext());

        store.TryGetBool(RouterSettingsStore.AdaptiveRoutingEnabledKey, out var storedEnabled).Should().BeTrue();
        storedEnabled.Should().BeTrue();
        store.TryGetInt(RouterSettingsStore.EmbeddingMemoryCapacityKey, out var storedCapacity).Should().BeTrue();
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
    public async Task UpdateRouterSettings_CapacityOutOfRange_RejectsWithInvalidArgumentRatherThanClamping(int outOfRangeCapacity)
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        var act = () => service.UpdateRouterSettings(
            new Contract.UpdateRouterSettingsRequest { AdaptiveRoutingEnabled = true, EmbeddingMemoryCapacity = outOfRangeCapacity },
            CreateContext());

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        // Rejected, not clamped: nothing should have been written.
        store.TryGetInt(RouterSettingsStore.EmbeddingMemoryCapacityKey, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(50_000)]
    public async Task UpdateRouterSettings_CapacityAtInclusiveBounds_Accepted(int boundaryCapacity)
    {
        var store = CreateStore();
        var service = CreateService(store: store);

        await service.UpdateRouterSettings(
            new Contract.UpdateRouterSettingsRequest { AdaptiveRoutingEnabled = false, EmbeddingMemoryCapacity = boundaryCapacity },
            CreateContext());

        store.TryGetInt(RouterSettingsStore.EmbeddingMemoryCapacityKey, out var storedCapacity).Should().BeTrue();
        storedCapacity.Should().Be(boundaryCapacity);
    }

    [Fact]
    public async Task GetRouterSettings_ReportsTheJudgeSettingsAndTheEligibleBackboneList()
    {
        var service = CreateService(
            judgeMonitor: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true, ModelName = "free-judge" }));

        var response = await service.GetRouterSettings(new Contract.GetRouterSettingsRequest(), CreateContext());

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
            new Contract.UpdateRouterSettingsRequest
            {
                AdaptiveRoutingEnabled = false,
                EmbeddingMemoryCapacity = 20_000,
                JudgeEnabled = true,
                JudgeModelName = "free-judge",
            },
            CreateContext());

        store.TryGetBool(RouterSettingsStore.JudgeEnabledKey, out var enabled).Should().BeTrue();
        enabled.Should().BeTrue();
        store.TryGetString(RouterSettingsStore.JudgeModelNameKey, out var modelName).Should().BeTrue();
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
            new Contract.UpdateRouterSettingsRequest { EmbeddingMemoryCapacity = 20_000, JudgeModelName = string.Empty },
            CreateContext());

        store.TryGetString(RouterSettingsStore.JudgeModelNameKey, out var modelName).Should().BeTrue();
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
            new Contract.UpdateRouterSettingsRequest { EmbeddingMemoryCapacity = 20_000, JudgeModelName = "not-a-free-model" },
            CreateContext());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        store.TryGetString(RouterSettingsStore.JudgeModelNameKey, out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_ThrowsOnNullStore()
    {
        var act = () => new RouterSettingsAdminGrpcService(
            null!,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            CreateJudgeModelSelector(),
            new RouterSettingsReloadToken(),
            NullLogger<RouterSettingsAdminGrpcService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    private static RouterSettingsAdminGrpcService CreateService(
        RouterSettingsStore? store = null,
        RouterSettingsReloadToken? reloadToken = null,
        StaticOptionsMonitor<RoutingOptions>? monitor = null,
        StaticOptionsMonitor<JudgeOptions>? judgeMonitor = null,
        JudgeModelSelector? judgeModelSelector = null) =>
        new(
            store ?? CreateStore(),
            monitor ?? new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            judgeMonitor ?? new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            judgeModelSelector ?? CreateJudgeModelSelector(),
            reloadToken ?? new RouterSettingsReloadToken(),
            NullLogger<RouterSettingsAdminGrpcService>.Instance);

    /// <summary>
    /// A selector over one free model, so the judge-model validation has something eligible to accept.
    /// Pass <paramref name="freeModelName"/> as null for the no-free-provider case.
    /// </summary>
    private static JudgeModelSelector CreateJudgeModelSelector(string? freeModelName = "free-judge") =>
        new(
            freeModelName is null
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
            new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            NullLogger<JudgeModelSelector>.Instance);

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "router_embedding_memory.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database, NullLogger<RouterSettingsStore>.Instance);
    }
}
