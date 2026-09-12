using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="CostReconciliationAdminGrpcService"/> (§5.8): reporting every configured provider's
/// checkpoint and most recent snapshot, and that "Run Now" actually runs a cycle before reporting.
/// </summary>
public sealed class CostReconciliationAdminGrpcServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServerCallContext CreateContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: Ct,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    private static CostReconciliationAdminGrpcService CreateService(
        TempDatabase temp,
        IReadOnlyList<IProviderCostReconciler> reconcilers,
        out IProviderCostReconciliationStore store)
    {
        store = temp.CreateCostReconciliationStore();
        var reconciliationService = new CostReconciliationService(
            reconcilers: reconcilers,
            usageLedger: temp.CreateUsageLedger(),
            store: store,
            options: Options.Create(new CostReconciliationOptions()),
            logger: NullLogger<CostReconciliationService>.Instance);

        return new CostReconciliationAdminGrpcService(store: store, reconciliationService: reconciliationService,
            reconcilers: reconcilers);
    }

    [Fact]
    public async Task GetCostReconciliationStatus_NoConfiguredProviders_ReturnsEmptyList()
    {
        using var temp = new TempDatabase();
        var service = CreateService(temp: temp, reconcilers: [], store: out _);

        var response = await service.GetCostReconciliationStatus(
            request: new Contract.GetCostReconciliationStatusRequest(), context: CreateContext());

        Assert.Empty(response.Providers);
    }

    [Fact]
    public async Task GetCostReconciliationStatus_ProviderWithNoSnapshotYet_ReportsNoOptionalFields()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler(provider: "openai");
        var service = CreateService(temp: temp, reconcilers: [reconciler], store: out _);

        var response = await service.GetCostReconciliationStatus(
            request: new Contract.GetCostReconciliationStatusRequest(), context: CreateContext());

        var status = Assert.Single(response.Providers);
        Assert.Equal(expected: "openai", actual: status.Provider);
        Assert.False(status.HasLastReconciledDay);
        Assert.False(status.HasLastReportedCostUsd);
        Assert.False(status.HasLastLocalCostUsd);
        Assert.Null(status.LastFetchedAtUtc);
    }

    [Fact]
    public async Task GetCostReconciliationStatus_ProviderWithSnapshot_ReportsCheckpointAndCosts()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler(provider: "openai");
        var service = CreateService(temp: temp, reconcilers: [reconciler], store: out var store);
        var windowStart = new DateTimeOffset(2026, 1, 15, 0, 0, 0, offset: TimeSpan.Zero);
        store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: "openai", WindowStartUtc: windowStart, WindowEndUtc: windowStart.AddDays(1),
            ProviderReportedCostUsd: 10.50m, LocalEstimatedCostUsd: 9.75m, ScopeNote: "scope",
            FetchedAtUtc: windowStart.AddHours(1)));
        store.SetLastReconciledDay(provider: "openai", day: DateOnly.FromDateTime(windowStart.UtcDateTime));

        var response = await service.GetCostReconciliationStatus(
            request: new Contract.GetCostReconciliationStatusRequest(), context: CreateContext());

        var status = Assert.Single(response.Providers);
        Assert.Equal(expected: "2026-01-15", actual: status.LastReconciledDay);
        Assert.Equal(expected: "10.50", actual: status.LastReportedCostUsd);
        Assert.Equal(expected: "9.75", actual: status.LastLocalCostUsd);
        Assert.Equal(expected: windowStart.AddHours(1), actual: status.LastFetchedAtUtc?.ToDateTimeOffset());
    }

    [Fact]
    public async Task RunCostReconciliationNow_RunsACycle_AndReportsTheResultingStatus()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler(provider: "openai");
        var service = CreateService(temp: temp, reconcilers: [reconciler], store: out var store);

        var response = await service.RunCostReconciliationNow(
            request: new Contract.RunCostReconciliationNowRequest(), context: CreateContext());

        Assert.True(reconciler.WasCalled);
        var status = Assert.Single(response.Providers);
        Assert.Equal(expected: "openai", actual: status.Provider);
        Assert.True(status.HasLastReconciledDay);
        Assert.NotNull(store.GetLastReconciledDay("openai"));
    }

    private sealed class FakeCostReconciler(string provider) : IProviderCostReconciler
    {
        public bool WasCalled { get; private set; }

        public string Provider => provider;

        public Task<decimal> GetReportedCostAsync(DateOnly day, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(1m);
        }
    }
}
