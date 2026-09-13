using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.Telemetry;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

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
        var from = ParseTimestamp(request.From, fieldName: "from");
        var to = ParseTimestamp(request.To, fieldName: "to");
        var result = await _reportingService.GetRoutingRoiAsync(
            from: from,
            to: to,
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
    private IReadOnlyList<UsageRollupBucket> QueryRollup(Timestamp? from, Timestamp? to, string width, string groupBy)
    {
        var result = _reportingService.GetUsageRollup(
            from: ParseTimestamp(from, fieldName: "from"),
            to: ParseTimestamp(to, fieldName: "to"),
            width: string.IsNullOrEmpty(width) ? "day" : width,
            groupBy: string.IsNullOrEmpty(groupBy) ? "day" : groupBy);
        return Unwrap(result);
    }

    /// <summary>
    /// Converts a wire <see cref="Timestamp"/> field to a <see cref="DateTimeOffset"/>, rejecting an absent
    /// field or an out-of-range value with the same <c>INVALID_ARGUMENT</c> contract the REST-era endpoint
    /// enforced via its own ISO-8601 parsing, rather than letting a missing/malformed value surface as an
    /// unhandled null-reference or framework exception.
    /// </summary>
    /// <param name="timestamp">The wire timestamp, or <see langword="null"/> when the client omitted it.</param>
    /// <param name="fieldName">The request field's name, for the rejection message.</param>
    private static DateTimeOffset ParseTimestamp(Timestamp? timestamp, string fieldName)
    {
        if (timestamp is null)
            throw new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
                detail: $"'{fieldName}' is required."));

        try
        {
            return timestamp.ToDateTimeOffset();
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(statusCode: StatusCode.InvalidArgument,
                detail: $"'{fieldName}' is not a valid timestamp: {ex.Message}"));
        }
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

    /// <summary>Projects a single <see cref="UsageRollupBucket"/> into its wire shape.</summary>
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

    /// <summary>Projects a single <see cref="RoutingRoiPoint"/> into its wire shape.</summary>
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
