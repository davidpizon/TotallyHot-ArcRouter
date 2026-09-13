using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A thin, platform-agnostic gRPC client for the proxy's <see cref="Contract.UsageAdminService"/>
/// (Phase 4, §5.15; docs/router/tracked-todos.md #7 - replaces the earlier plain-HTTP/JSON
/// <c>/admin/usage/*</c> client). Mirrors <see cref="ProviderAdminClient"/>'s shape exactly - same
/// channel/token setup, same error handling - so both clients read as one family;
/// <c>Gui.Services.UsageStore</c> wraps an instance of this the way <c>ProviderAdminStore</c> wraps
/// <see cref="ProviderAdminClient"/>. Every public method's signature is unchanged from the HTTP-era
/// client.
/// </summary>
public sealed class UsageQueryClient
{
    private readonly string? _adminToken;
    private readonly Contract.UsageAdminService.UsageAdminServiceClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageQueryClient"/> class.
    /// </summary>
    /// <param name="channel">
    /// The gRPC channel to send requests over. Must target the proxy's TLS gRPC endpoint (e.g.
    /// <c>https://localhost:5002</c>) - the same channel <see cref="ProviderAdminClient"/> uses.
    /// </param>
    /// <param name="adminToken">
    /// Optional management token; when set, it is sent in the <c>x-admin-token</c> gRPC metadata entry on
    /// every call.
    /// </param>
    public UsageQueryClient(GrpcChannel channel, string? adminToken = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _client = new Contract.UsageAdminService.UsageAdminServiceClient(channel);
        _adminToken = adminToken;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageQueryClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server - see
    /// <see cref="ProviderAdminClient"/>'s identical constructor for the full rationale. The caller owns
    /// any channel backing <paramref name="client"/>.
    /// </summary>
    /// <param name="client">The generated client (or test double) to send requests through.</param>
    /// <param name="adminToken">Optional management token; see the primary constructor's remarks.</param>
    public UsageQueryClient(Contract.UsageAdminService.UsageAdminServiceClient client, string? adminToken = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _adminToken = adminToken;
    }

    /// <summary>Gets totals for a preset window - the header ticker and summary tiles.</summary>
    /// <param name="window">One of <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, or <c>"all"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">Usage rollups are unavailable or the request failed.</exception>
    public async Task<UsageSummaryView> GetSummaryAsync(string window, CancellationToken cancellationToken = default)
    {
        var request = new Contract.GetUsageSummaryRequest { Window = window };
        var response = await CallAsync((client, options) => client.GetUsageSummaryAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return new UsageSummaryView(
            Requests: response.Requests,
            UnpricedRequests: response.UnpricedRequests,
            PromptTokens: response.PromptTokens,
            CompletionTokens: response.CompletionTokens,
            CacheCreationTokens: response.CacheCreationTokens,
            CacheReadTokens: response.CacheReadTokens,
            CostUsd: decimal.Parse(response.CostUsd, CultureInfo.InvariantCulture));
    }

    /// <summary>Gets the Model Distribution / Cost Analytics chart feed over an explicit range.</summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">
    /// Usage rollups are unavailable, the range/parameters were rejected, or the
    /// request failed.
    /// </exception>
    public async Task<IReadOnlyList<UsageRollupBucketView>> GetRollupAsync(
        DateTimeOffset from, DateTimeOffset to, string width, string groupBy,
        CancellationToken cancellationToken = default)
    {
        var request = new Contract.GetUsageRollupRequest
        {
            From = Timestamp.FromDateTimeOffset(from), To = Timestamp.FromDateTimeOffset(to),
            Width = width, GroupBy = groupBy
        };
        var response = await CallAsync((client, options) => client.GetUsageRollupAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return response.Buckets.Select(ToView).ToList();
    }

    /// <summary>
    /// Gets the Cost Analytics "Routing ROI" feed over an explicit range
    /// (docs/router/self-organizing-classification-plan.md Phase T4).
    /// </summary>
    /// <param name="from">Inclusive lower bound on comparison time.</param>
    /// <param name="to">Exclusive upper bound.</param>
    /// <param name="sessionId">A session to filter to, or <see langword="null"/> for every session.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">Comparisons are unavailable, the range was rejected, or the request failed.</exception>
    public async Task<IReadOnlyList<RoutingRoiPointView>> GetRoutingRoiAsync(
        DateTimeOffset from, DateTimeOffset to, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var request = new Contract.GetRoutingRoiRequest
        {
            From = Timestamp.FromDateTimeOffset(from), To = Timestamp.FromDateTimeOffset(to)
        };
        if (!string.IsNullOrEmpty(sessionId)) request.SessionId = sessionId;

        var response = await CallAsync((client, options) => client.GetRoutingRoiAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        return response.Entries.Select(ToView).ToList();
    }

    /// <summary>
    /// Streams the same rollup buckets <see cref="GetRollupAsync"/> returns for an explicit range, for
    /// rendering a CSV/JSON download without buffering the whole range client-side first via a separate
    /// bulk call (§5.12). The caller assembles the presentation format (CSV text, a JSON array, etc.) from
    /// the stream itself.
    /// </summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ProviderAdminException">Usage rollups are unavailable, the range/parameters were rejected, or the request failed.</exception>
    public async IAsyncEnumerable<UsageRollupBucketView> ExportRollupAsync(
        DateTimeOffset from, DateTimeOffset to, string width, string groupBy,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new Contract.ExportUsageRollupRequest
        {
            From = Timestamp.FromDateTimeOffset(from), To = Timestamp.FromDateTimeOffset(to),
            Width = width, GroupBy = groupBy
        };

        using var call = _client.ExportUsageRollup(request, BuildCallOptions(cancellationToken));
        var stream = call.ResponseStream;
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await stream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw ToProviderAdminException(ex);
            }

            if (!hasNext) yield break;
            yield return ToView(stream.Current);
        }
    }

    /// <summary>
    /// Attaches the admin token (if configured) as gRPC call metadata, invokes <paramref name="call"/>, and
    /// translates an <see cref="RpcException"/> into a <see cref="ProviderAdminException"/> carrying the
    /// same human-readable message the REST client used to surface. Identical to
    /// <see cref="ProviderAdminClient"/>'s private helper of the same shape.
    /// </summary>
    private async Task<TResponse> CallAsync<TResponse>(
        Func<Contract.UsageAdminService.UsageAdminServiceClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
    {
        var options = BuildCallOptions(cancellationToken);
        try
        {
            return await call(_client, options).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw ToProviderAdminException(ex);
        }
    }

    /// <summary>Builds the <see cref="CallOptions"/> shared by every call: the admin-token metadata entry and cancellation.</summary>
    private CallOptions BuildCallOptions(CancellationToken cancellationToken)
    {
        var metadata = new Metadata();
        if (!string.IsNullOrEmpty(_adminToken)) metadata.Add(key: "x-admin-token", value: _adminToken);
        return new CallOptions(headers: metadata, cancellationToken: cancellationToken);
    }

    /// <summary>Translates a gRPC failure into a <see cref="ProviderAdminException"/>, mirroring the REST client's error shape.</summary>
    private static ProviderAdminException ToProviderAdminException(RpcException ex)
    {
        // Unavailable is what Grpc.Net.Client reports for a transport-level failure - see
        // ProviderAdminClient.ToProviderAdminException's identical remark.
        return ex.StatusCode == StatusCode.Unavailable
            ? new ProviderAdminException(message: $"Could not reach the proxy management API: {ex.Status.Detail}",
                innerException: ex)
            : new ProviderAdminException(ex.Status.Detail);
    }

    private static UsageRollupBucketView ToView(Contract.UsageRollupBucketRow bucket)
    {
        return new UsageRollupBucketView(
            BucketStartUtc: bucket.BucketStartUtc.ToDateTimeOffset(),
            BucketWidth: bucket.BucketWidth,
            GroupKey: bucket.GroupKey,
            Requests: bucket.Requests,
            UnpricedRequests: bucket.UnpricedRequests,
            PromptTokens: bucket.PromptTokens,
            CompletionTokens: bucket.CompletionTokens,
            CacheCreationTokens: bucket.CacheCreationTokens,
            CacheReadTokens: bucket.CacheReadTokens,
            CostUsd: decimal.Parse(bucket.CostUsd, CultureInfo.InvariantCulture));
    }

    private static RoutingRoiPointView ToView(Contract.RoutingRoiEntry entry)
    {
        return new RoutingRoiPointView(
            ComparedAtUtc: entry.ComparedAtUtc.ToDateTimeOffset(),
            SessionId: entry.SessionId,
            RoutedModel: entry.RoutedModel,
            BaselineModel: entry.HasBaselineModel ? entry.BaselineModel : null,
            ActualCostUsd: entry.HasActualCostUsd ? decimal.Parse(entry.ActualCostUsd, CultureInfo.InvariantCulture) : null,
            BaselineEstimatedCostUsd: entry.HasBaselineEstimatedCostUsd
                ? decimal.Parse(entry.BaselineEstimatedCostUsd, CultureInfo.InvariantCulture)
                : null,
            EstimatedNetSavingsUsd: entry.HasEstimatedNetSavingsUsd
                ? decimal.Parse(entry.EstimatedNetSavingsUsd, CultureInfo.InvariantCulture)
                : null,
            IsExploratory: entry.IsExploratory);
    }
}
