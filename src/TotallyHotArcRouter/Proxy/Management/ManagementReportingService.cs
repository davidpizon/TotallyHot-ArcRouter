using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The read-only reporting surface split out of <see cref="ManagementFacade"/>
/// (docs/router/code-smell-refactoring-plan.md Phase 3 step 1): usage summaries, the cost-analytics chart
/// feed, and routing-ROI comparisons. None of these grant capability or mutate anything, so - unlike the
/// rest of <see cref="ManagementFacade"/> - they are not part of its documented "single security boundary"
/// for management operations; splitting them out cuts that class roughly in half without touching its
/// write/security-sensitive surface at all.
/// </summary>
public sealed class ManagementReportingService
{
    private readonly IUsageRollupStore? _rollupStore;
    private readonly ITaxonomyComparisonStore? _comparisonStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementReportingService"/> class.
    /// </summary>
    /// <param name="rollupStore">Optional usage-rollup store backing <see cref="GetUsageSummary"/> and <see cref="GetUsageRollup"/>. <see langword="null"/> makes both report <see cref="ManagementErrorType.Unavailable"/>.</param>
    /// <param name="comparisonStore">Optional taxonomy-comparison store backing <see cref="GetRoutingRoiAsync"/>. <see langword="null"/> makes it report <see cref="ManagementErrorType.Unavailable"/>.</param>
    public ManagementReportingService(IUsageRollupStore? rollupStore, ITaxonomyComparisonStore? comparisonStore)
    {
        _rollupStore = rollupStore;
        _comparisonStore = comparisonStore;
    }

    /// <summary>
    /// Totals over a preset window for the header ticker and summary tiles (Phase 4, §5.15). Backed by
    /// <see cref="IUsageRollupStore.Summary"/>.
    /// </summary>
    /// <param name="window">One of <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, or <c>"all"</c>.</param>
    public ManagementResult<UsageSummary> GetUsageSummary(string window)
    {
        if (_rollupStore is null)
        {
            return ManagementResult<UsageSummary>.Fail(ManagementErrorType.Unavailable, "Usage rollups are not available.");
        }

        var now = DateTimeOffset.UtcNow;

        // Aligned to a UTC day boundary, not just "now minus N" - UsageRollupStore.Summary reads whole
        // P1D buckets keyed by bucket_start_utc, so an unaligned 'from' (e.g. now.AddDays(-1), which lands
        // mid-day) would fall after yesterday's bucket start and exclude that fully-elapsed bucket entirely.
        var todayStartUtc = new DateTimeOffset(now.Date, TimeSpan.Zero);
        DateTimeOffset from;
        switch (window)
        {
            case "day":
                from = todayStartUtc.AddDays(-1);
                break;
            case "week":
                from = todayStartUtc.AddDays(-7);
                break;
            case "month":
                from = todayStartUtc.AddMonths(-1);
                break;
            case "all":
                from = DateTimeOffset.UnixEpoch;
                break;
            default:
                return ManagementResult<UsageSummary>.Fail(ManagementErrorType.InvalidRequest, "window must be 'day', 'week', 'month', or 'all'.");
        }

        return ManagementResultExecutor.TryExecute(() => _rollupStore.Summary(from, now), "Failed to read the usage summary.");
    }

    /// <summary>
    /// The Model Distribution / Cost Analytics chart feed (Phase 4, §5.15). Backed by
    /// <see cref="IUsageRollupStore.Query"/>.
    /// </summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end; must be after <paramref name="from"/>.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    public ManagementResult<IReadOnlyList<UsageRollupBucket>> GetUsageRollup(DateTimeOffset from, DateTimeOffset to, string width, string groupBy)
    {
        if (_rollupStore is null)
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.Unavailable, "Usage rollups are not available.");
        }

        if (to <= from)
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.InvalidRequest, "'to' must be after 'from'.");
        }

        string bucketWidth;
        switch (width)
        {
            case "hour":
                bucketWidth = "PT1H";
                break;
            case "day":
                bucketWidth = "P1D";
                break;
            default:
                return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.InvalidRequest, "width must be 'hour' or 'day'.");
        }

        if (groupBy is not ("model" or "provider" or "day"))
        {
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(ManagementErrorType.InvalidRequest, "groupBy must be 'model', 'provider', or 'day'.");
        }

        return ManagementResultExecutor.TryExecute(
            () => _rollupStore.Query(from, to, bucketWidth, groupBy),
            "Failed to read usage rollups.");
    }

    /// <summary>
    /// The Cost Analytics "Routing ROI" feed (docs/router/self-organizing-classification-plan.md Phase
    /// T4): every taxonomy comparison in a range, optionally narrowed to one session.
    /// </summary>
    /// <param name="from">Inclusive lower bound on comparison time.</param>
    /// <param name="to">Exclusive upper bound; must be after <paramref name="from"/>.</param>
    /// <param name="sessionId">A session to filter to, or <see langword="null"/> for every session.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The matching points, oldest first.</returns>
    /// <remarks>
    /// Reports <see cref="ManagementErrorType.Unavailable"/> rather than an empty list when no comparison
    /// store is configured. The distinction matters: an empty list means "routing saved nothing measurable
    /// in this range", while unavailable means "nothing has been measured at all", and collapsing the two
    /// would let a disabled feature render as a break-even result.
    /// </remarks>
    public async Task<ManagementResult<IReadOnlyList<RoutingRoiPoint>>> GetRoutingRoiAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (_comparisonStore is null)
        {
            return ManagementResult<IReadOnlyList<RoutingRoiPoint>>.Fail(
                ManagementErrorType.Unavailable, "Routing ROI comparisons are not available.");
        }

        if (to <= from)
        {
            return ManagementResult<IReadOnlyList<RoutingRoiPoint>>.Fail(
                ManagementErrorType.InvalidRequest, "'to' must be after 'from'.");
        }

        return await ManagementResultExecutor.TryExecuteAsync<IReadOnlyList<RoutingRoiPoint>>(async () =>
        {
            var rows = await _comparisonStore.LoadSinceAsync(from, sessionId, cancellationToken).ConfigureAwait(false);
            return
            [
                .. rows
                    .Where(r => r.ComparedAtUtc < to)
                    .Select(r => new RoutingRoiPoint(
                        r.ComparedAtUtc,
                        r.SessionId,
                        r.RoutedModel,
                        r.BaselineModel,
                        r.ActualCostUsd,
                        r.BaselineEstimatedCostUsd,
                        r.EstimatedNetSavingsUsd,
                        r.IsExploratory)),
            ];
        }, "Failed to read routing ROI comparisons.");
    }
}
