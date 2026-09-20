using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A thin, platform-agnostic gRPC client for the proxy's <see cref="Contract.UsageAdminService"/>.
/// Same 3-constructor + CallAsync + Unavailable wrapping as <see cref="ProviderAdminClient"/>; both sit
/// on <see cref="GrpcAdminClientBase{TGeneratedClient}"/>. <c>UsageStore</c> wraps an instance of this
/// the way <c>ProviderAdminStore</c> wraps <see cref="ProviderAdminClient"/>.
/// </summary>
public sealed class UsageQueryClient
    : GrpcAdminClientBase<Contract.UsageAdminService.UsageAdminServiceClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UsageQueryClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="serverAddress">The proxy's gRPC endpoint.</param>
    public UsageQueryClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.UsageAdminService.UsageAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageQueryClient"/> class over a shared,
    /// already-authenticated call invoker. The caller owns the invoker's underlying channel.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public UsageQueryClient(CallInvoker callInvoker)
        : base(new Contract.UsageAdminService.UsageAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageQueryClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns
    /// the channel's lifetime.
    /// </summary>
    /// <param name="client">The generated client (or test double) to send requests through.</param>
    public UsageQueryClient(Contract.UsageAdminService.UsageAdminServiceClient client)
        : base(client)
    {
    }

    /// <summary>Gets totals for a preset window - the header ticker and summary tiles.</summary>
    /// <param name="window">One of <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, or <c>"all"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="GrpcAdminException">Usage rollups are unavailable or the request failed.</exception>
    public async Task<UsageSummaryView> GetSummaryAsync(string window, CancellationToken cancellationToken = default)
    {
        var request = new Contract.GetUsageSummaryRequest { Window = window };
        var response = await CallAsync(
            (client, ct) => client.GetUsageSummaryAsync(request, ct),
            "Could not read the usage summary",
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
    /// <exception cref="GrpcAdminException">
    /// Usage rollups are unavailable, the range/parameters were rejected, or the request failed.
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
        var response = await CallAsync(
            (client, ct) => client.GetUsageRollupAsync(request, ct),
            "Could not read usage rollups",
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
    /// <exception cref="GrpcAdminException">Comparisons are unavailable, the range was rejected, or the request failed.</exception>
    public async Task<IReadOnlyList<RoutingRoiPointView>> GetRoutingRoiAsync(
        DateTimeOffset from, DateTimeOffset to, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var request = new Contract.GetRoutingRoiRequest
        {
            From = Timestamp.FromDateTimeOffset(from), To = Timestamp.FromDateTimeOffset(to)
        };
        if (!string.IsNullOrEmpty(sessionId)) request.SessionId = sessionId;

        var response = await CallAsync(
            (client, ct) => client.GetRoutingRoiAsync(request, ct),
            "Could not read routing ROI",
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
    /// <exception cref="GrpcAdminException">Usage rollups are unavailable, the range/parameters were rejected, or the request failed.</exception>
    public async IAsyncEnumerable<UsageRollupBucketView> ExportRollupAsync(
        DateTimeOffset from, DateTimeOffset to, string width, string groupBy,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new Contract.ExportUsageRollupRequest
        {
            From = Timestamp.FromDateTimeOffset(from), To = Timestamp.FromDateTimeOffset(to),
            Width = width, GroupBy = groupBy
        };

        using var call = Client.ExportUsageRollup(request, cancellationToken: cancellationToken);
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
                throw Wrap(ex: ex, action: "Could not export usage rollups");
            }

            if (!hasNext) yield break;
            yield return ToView(stream.Current);
        }
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
