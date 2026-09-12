using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="UsageAdminGrpcService"/>: result-to-status mapping, timestamp presence/range
/// validation (an absent or out-of-range wire <see cref="Timestamp"/> must reject with
/// <see cref="StatusCode.InvalidArgument"/> rather than null-referencing or throwing an unhandled
/// <see cref="InvalidOperationException"/>), and export streaming. Constructs
/// <see cref="ManagementReportingService"/> directly, mirroring <see cref="ManagementReportingServiceTests"/>'s
/// own setup, rather than booting a full <c>ProxyServer</c>.
/// </summary>
public sealed class UsageAdminGrpcServiceTests
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

    [Fact]
    public async Task GetUsageSummary_NoRollupStore_ThrowsUnavailable()
    {
        var service = new UsageAdminGrpcService(new ManagementReportingService(null, null));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.GetUsageSummary(new Contract.GetUsageSummaryRequest { Window = "day" }, CreateContext()));

        Assert.Equal(expected: StatusCode.Unavailable, actual: ex.StatusCode);
    }

    [Fact]
    public async Task GetUsageSummary_WithData_ReturnsTotals()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        await ledger.RecordAsync(
            entry: new UsageLedgerEntry(
                SessionId: "sess-1", 1, Provider: "openai", RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4",
                100, 50, null, null, 1.5m, CostConfidence: CostConfidence.Catalog,
                OccurredAtUtc: DateTimeOffset.UtcNow.AddDays(-2), RequestId: Guid.NewGuid().ToString("N")),
            cancellationToken: Ct);
        var service = new UsageAdminGrpcService(new ManagementReportingService(rollupStore: rollup, null));

        var response = await service.GetUsageSummary(new Contract.GetUsageSummaryRequest { Window = "week" },
            CreateContext());

        Assert.Equal(1, response.Requests);
        Assert.Equal(expected: "1.5", actual: response.CostUsd);
    }

    [Fact]
    public async Task GetUsageRollup_MissingFromField_ThrowsInvalidArgument()
    {
        using var temp = new TempDatabase();
        var service = new UsageAdminGrpcService(new ManagementReportingService(rollupStore: temp.CreateRollupStore(),
            null));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetUsageRollup(
            new Contract.GetUsageRollupRequest { To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
        Assert.Contains(expectedSubstring: "from", actualString: ex.Status.Detail);
    }

    [Fact]
    public async Task GetUsageRollup_MissingToField_ThrowsInvalidArgument()
    {
        using var temp = new TempDatabase();
        var service = new UsageAdminGrpcService(new ManagementReportingService(rollupStore: temp.CreateRollupStore(),
            null));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetUsageRollup(
            new Contract.GetUsageRollupRequest { From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
        Assert.Contains(expectedSubstring: "to", actualString: ex.Status.Detail);
    }

    [Fact]
    public async Task GetRoutingRoi_MissingFromField_ThrowsInvalidArgument()
    {
        // Validation happens before ManagementReportingService.GetRoutingRoiAsync is ever called, so no
        // comparison store needs to be wired up for this case.
        var service = new UsageAdminGrpcService(new ManagementReportingService(null, null));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetRoutingRoi(
            new Contract.GetRoutingRoiRequest { To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
    }

    [Fact]
    public async Task ExportUsageRollup_StreamsOneMessagePerBucket()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        await ledger.RecordAsync(
            entry: new UsageLedgerEntry(
                SessionId: "sess-1", 1, Provider: "openai", RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4",
                100, 50, null, null, 1.5m, CostConfidence: CostConfidence.Catalog,
                OccurredAtUtc: DateTimeOffset.UtcNow.AddDays(-1), RequestId: Guid.NewGuid().ToString("N")),
            cancellationToken: Ct);
        var service = new UsageAdminGrpcService(new ManagementReportingService(rollupStore: rollup, null));
        var writer = new FakeServerStreamWriter<Contract.UsageRollupBucketRow>();

        await service.ExportUsageRollup(new Contract.ExportUsageRollupRequest
        {
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-7)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Width = "day",
            GroupBy = "day"
        }, writer, CreateContext());

        var row = Assert.Single(writer.Written);
        Assert.Equal(1, row.Requests);
        Assert.Equal(expected: "1.5", actual: row.CostUsd);
    }

    [Fact]
    public async Task ExportUsageRollup_MissingFromField_ThrowsInvalidArgument()
    {
        using var temp = new TempDatabase();
        var service = new UsageAdminGrpcService(new ManagementReportingService(rollupStore: temp.CreateRollupStore(),
            null));
        var writer = new FakeServerStreamWriter<Contract.UsageRollupBucketRow>();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.ExportUsageRollup(
            new Contract.ExportUsageRollupRequest { To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            writer, CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
    }

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }
}
