using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.Telemetry;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// gRPC service backing the Governance/Model Distribution/Cost Analytics GUI tabs' usage-query surface
/// (docs/router/tracked-todos.md #7). Replaces <see cref="UsageAdminEndpoints"/>'s plain-HTTP
/// <c>/admin/usage/*</c> surface, which shared the LLM-forwarding proxy port with real traffic; this
/// service is mapped onto the same loopback TLS endpoint as <c>TelemetryService</c> instead. All logic
/// lives in <see cref="ManagementReportingService"/>; this class only translates gRPC requests into
/// service calls and <see cref="ManagementResult{T}"/> outcomes into gRPC responses/status codes,
/// mirroring <see cref="ProviderAdminGrpcService"/>.
/// </summary>
public sealed class UsageAdminGrpcService : Contract.UsageAdminService.UsageAdminServiceBase
{
    private readonly ManagementReportingService _reportingService;

    /// <summary>Initializes a new instance of the <see cref="UsageAdminGrpcService"/> class.</summary>
    /// <param name="reportingService">The shared reporting service backing every read.</param>
    public UsageAdminGrpcService(ManagementReportingService reportingService)
    {
        ArgumentNullException.ThrowIfNull(reportingService);
        _reportingService = reportingService;
    }

    /// <inheritdoc/>
    public override Task<Contract.UsageSummaryResponse> GetUsageSummary(Contract.GetUsageSummaryRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = string.IsNullOrEmpty(request.Window) ? "day" : request.Window;
        var result = _reportingService.GetUsageSummary(window);
        var summary = Unwrap(result);
        return Task.FromResult(new Contract.UsageSummaryResponse
        {
            Requests = summary.Requests,
            UnpricedRequests = summary.UnpricedRequests,
            PromptTokens = summary.PromptTokens,
            CompletionTokens = summary.CompletionTokens,
            CacheCreationTokens = summary.CacheCreationTokens,
            CacheReadTokens = summary.CacheReadTokens,
            CostUsd = summary.CostUsd.ToString(CultureInfo.InvariantCulture)
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.UsageRollupResponse> GetUsageRollup(Contract.GetUsageRollupRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buckets = QueryRollup(request.From, request.To, request.Width, request.GroupBy);

        var response = new Contract.UsageRollupResponse();
        response.Buckets.AddRange(buckets.Select(ToWire));
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override async Task<Contract.RoutingRoiResponse> GetRoutingRoi(Contract.GetRoutingRoiRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _reportingService.GetRoutingRoiAsync(
            from: request.From.ToDateTimeOffset(),
            to: request.To.ToDateTimeOffset(),
            sessionId: request.HasSessionId ? request.SessionId : null,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        var points = Unwrap(result);

        var response = new Contract.RoutingRoiResponse();
        response.Entries.AddRange(points.Select(ToWire));
        return response;
    }

    /// <inheritdoc/>
    public override async Task ExportUsageRollup(Contract.ExportUsageRollupRequest request,
        IServerStreamWriter<Contract.UsageRollupBucketRow> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);

        var buckets = QueryRollup(request.From, request.To, request.Width, request.GroupBy);
        foreach (var bucket in buckets)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await responseStream.WriteAsync(ToWire(bucket)).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the shared rollup query with the request's range/width/groupBy, defaulting width/groupBy to "day".</summary>
    private IReadOnlyList<UsageRollupBucket> QueryRollup(Timestamp from, Timestamp to, string width, string groupBy)
    {
        var result = _reportingService.GetUsageRollup(
            from: from.ToDateTimeOffset(),
            to: to.ToDateTimeOffset(),
            width: string.IsNullOrEmpty(width) ? "day" : width,
            groupBy: string.IsNullOrEmpty(groupBy) ? "day" : groupBy);
        return Unwrap(result);
    }

    /// <summary>Returns a successful <see cref="ManagementResult{T}"/>'s value, or throws the equivalent <see cref="RpcException"/>.</summary>
    private static T Unwrap<T>(ManagementResult<T> result)
    {
        if (result.Success) return result.Value!;

        var statusCode = result.ErrorType switch
        {
            ManagementErrorType.NotFound => StatusCode.NotFound,
            ManagementErrorType.InvalidRequest => StatusCode.InvalidArgument,
            ManagementErrorType.Unavailable => StatusCode.Unavailable,
            _ => StatusCode.Internal
        };
        throw new RpcException(new Status(statusCode: statusCode, detail: result.ErrorMessage!));
    }

    private static Contract.UsageRollupBucketRow ToWire(UsageRollupBucket bucket)
    {
        return new Contract.UsageRollupBucketRow
        {
            BucketStartUtc = Timestamp.FromDateTimeOffset(bucket.BucketStartUtc),
            BucketWidth = bucket.BucketWidth,
            GroupKey = bucket.GroupKey,
            Requests = bucket.Requests,
            UnpricedRequests = bucket.UnpricedRequests,
            PromptTokens = bucket.PromptTokens,
            CompletionTokens = bucket.CompletionTokens,
            CacheCreationTokens = bucket.CacheCreationTokens,
            CacheReadTokens = bucket.CacheReadTokens,
            CostUsd = bucket.CostUsd.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static Contract.RoutingRoiEntry ToWire(RoutingRoiPoint point)
    {
        var wire = new Contract.RoutingRoiEntry
        {
            ComparedAtUtc = Timestamp.FromDateTimeOffset(point.ComparedAtUtc),
            SessionId = point.SessionId,
            RoutedModel = point.RoutedModel,
            IsExploratory = point.IsExploratory
        };
        if (point.BaselineModel is not null) wire.BaselineModel = point.BaselineModel;
        if (point.ActualCostUsd.HasValue) wire.ActualCostUsd = point.ActualCostUsd.Value.ToString(CultureInfo.InvariantCulture);
        if (point.BaselineEstimatedCostUsd.HasValue)
            wire.BaselineEstimatedCostUsd = point.BaselineEstimatedCostUsd.Value.ToString(CultureInfo.InvariantCulture);
        if (point.EstimatedNetSavingsUsd.HasValue)
            wire.EstimatedNetSavingsUsd = point.EstimatedNetSavingsUsd.Value.ToString(CultureInfo.InvariantCulture);
        return wire;
    }
}
