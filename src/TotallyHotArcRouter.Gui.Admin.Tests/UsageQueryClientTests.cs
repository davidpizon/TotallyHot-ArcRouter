using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Unit coverage for <see cref="UsageQueryClient"/>: request-message mapping and response (de)serialization
/// against a stubbed generated client, plus error translation. Mirrors
/// <see cref="ProviderAdminClientTests"/> in structure - the two clients are intentional siblings - and
/// the generated-client test-double seam <c>TotallyHot.ArcRouter.Gui.Telemetry.PriceSourceAdminClientTests</c>
/// established for gRPC clients in this codebase.
/// </summary>
public sealed class UsageQueryClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- GetSummaryAsync ---

    [Fact]
    public async Task GetSummaryAsync_SendsTheWindow_AndMapsEveryField()
    {
        var stub = new StubClient
        {
            SummaryResponse = new Contract.UsageSummaryResponse
            {
                Requests = 120,
                UnpricedRequests = 5,
                PromptTokens = 80000,
                CompletionTokens = 20000,
                CacheCreationTokens = 1000,
                CacheReadTokens = 500,
                CostUsd = "3.75"
            }
        };
        var client = new UsageQueryClient(stub);

        var summary = await client.GetSummaryAsync(window: "day", cancellationToken: Ct);

        Assert.Equal(expected: "day", actual: stub.LastSummaryRequest!.Window);
        Assert.Equal(120L, actual: summary.Requests);
        Assert.Equal(5L, actual: summary.UnpricedRequests);
        Assert.Equal(80000L, actual: summary.PromptTokens);
        Assert.Equal(20000L, actual: summary.CompletionTokens);
        Assert.Equal(1000L, actual: summary.CacheCreationTokens);
        Assert.Equal(500L, actual: summary.CacheReadTokens);
        Assert.Equal(3.75m, actual: summary.CostUsd);
    }

    // --- GetRollupAsync ---

    [Fact]
    public async Task GetRollupAsync_SendsTheRangeAndBinning_AndMapsEveryBucketField()
    {
        var stub = new StubClient
        {
            CannedRollupResponse = RollupResponse(Bucket(bucketStartUtc: "2026-01-01T00:00:00Z", bucketWidth: "P1D",
                groupKey: "gpt-5.4", requests: 50, unpricedRequests: 2, promptTokens: 40000, completionTokens: 10000,
                cacheCreationTokens: 0, cacheReadTokens: 0, costUsd: "1.20"))
        };
        var client = new UsageQueryClient(stub);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-02-01T00:00:00Z");

        var buckets = await client.GetRollupAsync(from: from, to: to, width: "day", groupBy: "model",
            cancellationToken: Ct);

        Assert.Equal(expected: from, actual: stub.LastRollupRequest!.From.ToDateTimeOffset());
        Assert.Equal(expected: to, actual: stub.LastRollupRequest.To.ToDateTimeOffset());
        Assert.Equal(expected: "day", actual: stub.LastRollupRequest.Width);
        Assert.Equal(expected: "model", actual: stub.LastRollupRequest.GroupBy);

        var bucket = Assert.Single(buckets);
        Assert.Equal(expected: "gpt-5.4", actual: bucket.GroupKey);
        Assert.Equal(50L, actual: bucket.Requests);
        Assert.Equal(1.20m, actual: bucket.CostUsd);
        Assert.Equal(expected: "P1D", actual: bucket.BucketWidth);
    }

    [Fact]
    public async Task GetRollupAsync_EmptyResponse_ReturnsEmptyList()
    {
        var stub = new StubClient { CannedRollupResponse = new Contract.UsageRollupResponse() };
        var client = new UsageQueryClient(stub);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var buckets = await client.GetRollupAsync(from: from, to: from.AddDays(1), width: "hour",
            groupBy: "provider", cancellationToken: Ct);

        Assert.Empty(buckets);
    }

    // --- GetRoutingRoiAsync ---

    [Fact]
    public async Task GetRoutingRoiAsync_SendsTheRange_AndMapsTheCounterfactual()
    {
        var stub = new StubClient
        {
            CannedRoutingRoiResponse = RoutingRoiResponse(new Contract.RoutingRoiEntry
            {
                ComparedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-05T10:00:00Z")),
                SessionId = "session-7",
                RoutedModel = "kimi-k2.5",
                BaselineModel = "glm-5",
                ActualCostUsd = "0.02",
                BaselineEstimatedCostUsd = "0.11",
                EstimatedNetSavingsUsd = "0.09",
                IsExploratory = true
            })
        };
        var client = new UsageQueryClient(stub);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-02-01T00:00:00Z");

        var points = await client.GetRoutingRoiAsync(from: from, to: to, cancellationToken: Ct);

        Assert.False(stub.LastRoutingRoiRequest!.HasSessionId);
        var point = Assert.Single(points);
        Assert.Equal(expected: "session-7", actual: point.SessionId);
        Assert.Equal(expected: "kimi-k2.5", actual: point.RoutedModel);
        Assert.Equal(expected: "glm-5", actual: point.BaselineModel);
        Assert.Equal(0.09m, actual: point.EstimatedNetSavingsUsd);
        Assert.True(point.IsExploratory);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_SessionId_IsSentWhenProvided()
    {
        var stub = new StubClient { CannedRoutingRoiResponse = new Contract.RoutingRoiResponse() };
        var client = new UsageQueryClient(stub);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await client.GetRoutingRoiAsync(from: from, to: from.AddDays(1), sessionId: "s-1", cancellationToken: Ct);

        Assert.True(stub.LastRoutingRoiRequest!.HasSessionId);
        Assert.Equal(expected: "s-1", actual: stub.LastRoutingRoiRequest.SessionId);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_NullCostsRoundTripAsNullNotZero()
    {
        // An abstaining baseline yields absent optional fields; collapsing them to 0 would read as
        // "routing broke even".
        var stub = new StubClient
        {
            CannedRoutingRoiResponse = RoutingRoiResponse(new Contract.RoutingRoiEntry
            {
                ComparedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                SessionId = "s",
                RoutedModel = "kimi-k2.5",
                ActualCostUsd = "0.02",
                IsExploratory = false
            })
        };
        var client = new UsageQueryClient(stub);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var point = Assert.Single(await client.GetRoutingRoiAsync(from: from, to: from.AddDays(30),
            cancellationToken: Ct));

        Assert.Null(point.BaselineModel);
        Assert.Null(point.BaselineEstimatedCostUsd);
        Assert.Null(point.EstimatedNetSavingsUsd);
    }

    // --- ExportRollupAsync ---

    [Fact]
    public async Task ExportRollupAsync_StreamsEveryRow()
    {
        var stub = new StubClient
        {
            ExportRows =
            [
                Bucket(bucketStartUtc: "2026-01-01T00:00:00Z", bucketWidth: "P1D", groupKey: "day",
                    requests: 10, unpricedRequests: 0, promptTokens: 1000, completionTokens: 500,
                    cacheCreationTokens: 0, cacheReadTokens: 0, costUsd: "0.50"),
                Bucket(bucketStartUtc: "2026-01-02T00:00:00Z", bucketWidth: "P1D", groupKey: "day",
                    requests: 20, unpricedRequests: 0, promptTokens: 2000, completionTokens: 1000,
                    cacheCreationTokens: 0, cacheReadTokens: 0, costUsd: "1.00")
            ]
        };
        var client = new UsageQueryClient(stub);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var rows = new List<UsageRollupBucketView>();
        await foreach (var row in client.ExportRollupAsync(from: from, to: from.AddDays(2), width: "day",
                           groupBy: "day", cancellationToken: Ct))
            rows.Add(row);

        Assert.Equal(2, actual: rows.Count);
        Assert.Equal(0.50m, actual: rows[0].CostUsd);
        Assert.Equal(1.00m, actual: rows[1].CostUsd);
    }

    // --- admin token metadata ---

    [Fact]
    public async Task AdminToken_WhenConfigured_IsSentAsMetadata()
    {
        var stub = new StubClient { SummaryResponse = new Contract.UsageSummaryResponse { CostUsd = "0" } };
        var client = new UsageQueryClient(stub, adminToken: "s3cret");

        await client.GetSummaryAsync(window: "week", cancellationToken: Ct);

        var entry = Assert.Single(stub.LastCallOptions!.Value.Headers!.GetAll("x-admin-token"));
        Assert.Equal(expected: "s3cret", actual: entry.Value);
    }

    [Fact]
    public async Task AdminToken_WhenNotConfigured_IsNotSent()
    {
        var stub = new StubClient { SummaryResponse = new Contract.UsageSummaryResponse { CostUsd = "0" } };
        var client = new UsageQueryClient(stub);

        await client.GetSummaryAsync(window: "week", cancellationToken: Ct);

        Assert.Empty(stub.LastCallOptions!.Value.Headers!.GetAll("x-admin-token"));
    }

    // --- error handling ---

    [Fact]
    public async Task Unavailable_BecomesTheReachabilityMessage()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        var client = new UsageQueryClient(stub);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: Ct));

        Assert.Contains(expectedSubstring: "Could not reach the proxy management API", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
        Assert.IsType<RpcException>(ex.InnerException);
    }

    [Fact]
    public async Task ServerRejection_KeepsTheServersOwnDetail()
    {
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(statusCode: StatusCode.FailedPrecondition,
                detail: "Usage rollups are not available."))
        };
        var client = new UsageQueryClient(stub);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetSummaryAsync(window: "day", cancellationToken: Ct));

        Assert.Equal(expected: "Usage rollups are not available.", actual: ex.Message);
    }

    [Fact]
    public void Constructor_NullChannel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UsageQueryClient((Grpc.Net.Client.GrpcChannel)null!));
    }

    private static Contract.UsageRollupResponse RollupResponse(params Contract.UsageRollupBucketRow[] buckets)
    {
        var response = new Contract.UsageRollupResponse();
        response.Buckets.AddRange(buckets);
        return response;
    }

    private static Contract.RoutingRoiResponse RoutingRoiResponse(params Contract.RoutingRoiEntry[] entries)
    {
        var response = new Contract.RoutingRoiResponse();
        response.Entries.AddRange(entries);
        return response;
    }

    private static Contract.UsageRollupBucketRow Bucket(string bucketStartUtc, string bucketWidth, string groupKey,
        long requests, long unpricedRequests, long promptTokens, long completionTokens, long cacheCreationTokens,
        long cacheReadTokens, string costUsd)
    {
        return new Contract.UsageRollupBucketRow
        {
            BucketStartUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(bucketStartUtc)),
            BucketWidth = bucketWidth,
            GroupKey = groupKey,
            Requests = requests,
            UnpricedRequests = unpricedRequests,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CacheCreationTokens = cacheCreationTokens,
            CacheReadTokens = cacheReadTokens,
            CostUsd = costUsd
        };
    }

    private sealed class FakeStreamReader<T>(IReadOnlyList<T> messages) : IAsyncStreamReader<T>
    {
        private int _index = -1;

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            if (_index >= messages.Count) return Task.FromResult(false);

            Current = messages[_index];
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.UsageAdminService.UsageAdminServiceClient
    {
        public Contract.UsageSummaryResponse SummaryResponse { get; init; } = new();

        public Contract.UsageRollupResponse CannedRollupResponse { get; init; } = new();

        public Contract.RoutingRoiResponse CannedRoutingRoiResponse { get; init; } = new();

        public IReadOnlyList<Contract.UsageRollupBucketRow> ExportRows { get; init; } = [];

        public RpcException? Failure { get; init; }

        public Contract.GetUsageSummaryRequest? LastSummaryRequest { get; private set; }

        public Contract.GetUsageRollupRequest? LastRollupRequest { get; private set; }

        public Contract.GetRoutingRoiRequest? LastRoutingRoiRequest { get; private set; }

        public CallOptions? LastCallOptions { get; private set; }

        public override AsyncUnaryCall<Contract.UsageSummaryResponse> GetUsageSummaryAsync(
            Contract.GetUsageSummaryRequest request, CallOptions options)
        {
            LastSummaryRequest = request;
            LastCallOptions = options;
            return Call(SummaryResponse);
        }

        public override AsyncUnaryCall<Contract.UsageRollupResponse> GetUsageRollupAsync(
            Contract.GetUsageRollupRequest request, CallOptions options)
        {
            LastRollupRequest = request;
            LastCallOptions = options;
            return Call(CannedRollupResponse);
        }

        public override AsyncUnaryCall<Contract.RoutingRoiResponse> GetRoutingRoiAsync(
            Contract.GetRoutingRoiRequest request, CallOptions options)
        {
            LastRoutingRoiRequest = request;
            LastCallOptions = options;
            return Call(CannedRoutingRoiResponse);
        }

        public override AsyncServerStreamingCall<Contract.UsageRollupBucketRow> ExportUsageRollup(
            Contract.ExportUsageRollupRequest request, CallOptions options)
        {
            LastCallOptions = options;
            IAsyncStreamReader<Contract.UsageRollupBucketRow> reader = Failure is null
                ? new FakeStreamReader<Contract.UsageRollupBucketRow>(ExportRows)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.UsageRollupBucketRow>(
                responseStream: reader,
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        private AsyncUnaryCall<T> Call<T>(T response)
        {
            return new AsyncUnaryCall<T>(
                responseAsync: Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        private sealed class ThrowingStreamReader(RpcException failure)
            : IAsyncStreamReader<Contract.UsageRollupBucketRow>
        {
            public Contract.UsageRollupBucketRow Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                return Task.FromException<bool>(failure);
            }
        }
    }
}
