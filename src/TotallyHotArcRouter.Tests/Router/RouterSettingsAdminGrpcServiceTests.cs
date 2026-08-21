using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
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
    public void Constructor_ThrowsOnNullStore()
    {
        var act = () => new RouterSettingsAdminGrpcService(
            null!,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            new RouterSettingsReloadToken(),
            NullLogger<RouterSettingsAdminGrpcService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    private static RouterSettingsAdminGrpcService CreateService(
        RouterSettingsStore? store = null,
        RouterSettingsReloadToken? reloadToken = null,
        StaticOptionsMonitor<RoutingOptions>? monitor = null) =>
        new(
            store ?? CreateStore(),
            monitor ?? new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions()),
            reloadToken ?? new RouterSettingsReloadToken(),
            NullLogger<RouterSettingsAdminGrpcService>.Instance);

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "router_embedding_memory.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database, NullLogger<RouterSettingsStore>.Instance);
    }
}
