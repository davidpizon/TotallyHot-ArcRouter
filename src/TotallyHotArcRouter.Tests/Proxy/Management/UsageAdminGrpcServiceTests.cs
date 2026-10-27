using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;
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
    public async Task GetRoutingRoi_ValidRange_ReturnsResponse()
    {
        // Web GUI migration plan Phase P2: this covers the RPC wire-mapping this class itself performs
        // for the success path - the business logic (ManagementReportingService.GetRoutingRoiAsync) is
        // already fully covered by ManagementReportingServiceTests, which this delegates to. An empty
        // comparison store is enough to exercise the wire response shape without duplicating that
        // coverage.
        var service = new UsageAdminGrpcService(
            new ManagementReportingService(null, comparisonStore: new EmptyComparisonStore()));

        var response = await service.GetRoutingRoi(new Contract.GetRoutingRoiRequest
        {
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        }, CreateContext());

        Assert.NotNull(response);
        Assert.Empty(response.Entries);
    }

    [Fact]
    public async Task GetLearningReportCard_MissingFromField_ThrowsInvalidArgument()
    {
        var service = new UsageAdminGrpcService(new ManagementReportingService(null, null));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetLearningReportCard(
            new Contract.GetLearningReportCardRequest { To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
    }

    [Fact]
    public async Task GetLearningReportCard_ValidRange_ReturnsResponse()
    {
        var service = new UsageAdminGrpcService(
            new ManagementReportingService(null, comparisonStore: new EmptyComparisonStore()));

        var response = await service.GetLearningReportCard(new Contract.GetLearningReportCardRequest
        {
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        }, CreateContext());

        Assert.NotNull(response);
        Assert.Empty(response.SpendByModel);
        Assert.Equal(5, actual: response.GradeMix.Count);
        Assert.Equal(expected: "0", actual: response.TotalSpendUsd);
        Assert.False(response.HasMeanScoreDelta);
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

    [Fact]
    public async Task GetGraderEpochChart_NullQuery_IsUnavailable()
    {
        var service = new UsageAdminGrpcService(new ManagementReportingService(null, null));
        var now = DateTimeOffset.UtcNow;

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetGraderEpochChart(
            new Contract.GetGraderEpochChartRequest
            {
                From = Timestamp.FromDateTimeOffset(now.AddHours(-1)),
                To = Timestamp.FromDateTimeOffset(now)
            },
            CreateContext()));

        Assert.Equal(expected: StatusCode.Unavailable, actual: ex.StatusCode);
        Assert.Equal(expected: "The grader epoch chart is not available.", actual: ex.Status.Detail);
    }

    [Fact]
    public async Task GetGraderEpochChart_MissingFrom_IsInvalidArgument()
    {
        var service = new UsageAdminGrpcService(new ManagementReportingService(null, null));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetGraderEpochChart(
            new Contract.GetGraderEpochChartRequest { To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
        Assert.Equal(expected: "'from' is required.", actual: ex.Status.Detail);
    }

    [Fact]
    public async Task GetGraderEpochChart_GapOmitsBothRatios()
    {
        using var files = new GapFiles();
        var service = new UsageAdminGrpcService(
            new ManagementReportingService(null, null),
            new GraderEpochChartQuery(files.Epochs, files.Transcripts, files.Memory));

        var response = await service.GetGraderEpochChart(
            new Contract.GetGraderEpochChartRequest
            {
                From = Timestamp.FromDateTimeOffset(files.From),
                To = Timestamp.FromDateTimeOffset(files.To),
                Width = "hour"
            },
            CreateContext());

        var point = Assert.Single(response.Points);
        Assert.False(point.HasTokensPerDollar);
        Assert.False(point.HasDollarsPerThousandTokens);
        Assert.NotEqual(expected: "0", actual: point.TokensPerDollar);
        Assert.NotEqual(expected: "0", actual: point.DollarsPerThousandTokens);
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

    /// <summary>An always-empty <see cref="ITaxonomyComparisonStore"/>, for wire-mapping tests that don't care about the data itself.</summary>
    private sealed class EmptyComparisonStore : ITaxonomyComparisonStore
    {
        public Task<IReadOnlyList<long>> LoadPendingComparisonsAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task UpsertAsync(TaxonomyComparisonRecord record, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaxonomyComparisonRecord>> LoadSinceAsync(
            DateTimeOffset since, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TaxonomyComparisonRecord>>([]);
        }
    }

    /// <summary>One null-cost transcript in its own pair of database files. The gap must not become "0".</summary>
    private sealed class GapFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(path1: TestScratchDirectory.RunRoot, path2: Guid.NewGuid().ToString("N"));

        public GapFiles()
        {
            Directory.CreateDirectory(_directory);
            From = new DateTimeOffset(year: 2026, month: 10, day: 26, hour: 10, minute: 0, second: 0, offset: TimeSpan.Zero);
            To = From.AddHours(1);
            Transcripts = new TranscriptDatabase(Options.Create(new StorageOptions
            {
                TranscriptDatabasePath = Path.Combine(path1: _directory, path2: "transcripts.db")
            }));
            Memory = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(path1: _directory, path2: "router.db")
            }));
            Memory.EnsureCreated();
            Epochs = new SqliteGraderEpochEventStore(Memory);
            Transcripts.EnsureCreated();
            using var connection = Transcripts.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO request_transcripts (
                                      correlation_id, created_at_utc, requested_model, routed_model,
                                      is_utility, is_exploratory, propensity, input_tokens, output_tokens, cost)
                                  VALUES ('gap', $at, 'm', 'm', 0, 0, 0, 10, 10, NULL);
                                  """;
            command.Parameters.AddWithValue(
                parameterName: "$at",
                value: From.AddMinutes(5).ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public DateTimeOffset From { get; }

        public DateTimeOffset To { get; }

        public TranscriptDatabase Transcripts { get; }

        public RouterMemoryDatabase Memory { get; }

        public SqliteGraderEpochEventStore Epochs { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(path: _directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
