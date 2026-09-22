using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The read-only reporting surface split out of <see cref="ManagementFacade"/>
/// (docs/router/code-smell-refactoring-plan.md Phase 3 step 1): usage summaries, the cost-analytics chart
/// feed, routing-ROI comparisons, and the Report Card snapshot (spend / grade mix / score delta). None of these grant capability or mutate anything, so - unlike the
/// rest of <see cref="ManagementFacade"/> - they are not part of its documented "single security boundary"
/// for management operations; splitting them out cuts that class roughly in half without touching its
/// write/security-sensitive surface at all.
/// </summary>
public sealed class ManagementReportingService
{
    private readonly ITaxonomyComparisonStore? _comparisonStore;
    private readonly IUsageRollupStore? _rollupStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementReportingService"/> class.
    /// </summary>
    /// <param name="rollupStore">
    /// Optional usage-rollup store backing <see cref="GetUsageSummary"/> and
    /// <see cref="GetUsageRollup"/>. <see langword="null"/> makes both report <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
    /// <param name="comparisonStore">
    /// Optional taxonomy-comparison store backing <see cref="GetRoutingRoiAsync"/>.
    /// <see langword="null"/> makes it report <see cref="ManagementErrorType.Unavailable"/>.
    /// </param>
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
            return ManagementResult<UsageSummary>.Fail(errorType: ManagementErrorType.Unavailable,
                message: "Usage rollups are not available.");

        var now = DateTimeOffset.UtcNow;

        // Aligned to a UTC day boundary, not just "now minus N" - UsageRollupStore.Summary reads whole
        // P1D buckets keyed by bucket_start_utc, so an unaligned 'from' (e.g. now.AddDays(-1), which lands
        // mid-day) would fall after yesterday's bucket start and exclude that fully-elapsed bucket entirely.
        var todayStartUtc = new DateTimeOffset(dateTime: now.Date, offset: TimeSpan.Zero);
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
                return ManagementResult<UsageSummary>.Fail(errorType: ManagementErrorType.InvalidRequest,
                    message: "window must be 'day', 'week', 'month', or 'all'.");
        }

        return ManagementResultExecutor.TryExecute(action: () => _rollupStore.Summary(from: from, to: now),
            failureMessage: "Failed to read the usage summary.");
    }

    /// <summary>
    /// The Model Distribution / Cost Analytics chart feed (Phase 4, §5.15). Backed by
    /// <see cref="IUsageRollupStore.Query"/>.
    /// </summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end; must be after <paramref name="from"/>.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    public ManagementResult<IReadOnlyList<UsageRollupBucket>> GetUsageRollup(DateTimeOffset from, DateTimeOffset to,
        string width, string groupBy)
    {
        if (_rollupStore is null)
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(errorType: ManagementErrorType.Unavailable,
                message: "Usage rollups are not available.");

        if (to <= from)
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(
                errorType: ManagementErrorType.InvalidRequest, message: "'to' must be after 'from'.");

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
                return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(
                    errorType: ManagementErrorType.InvalidRequest, message: "width must be 'hour' or 'day'.");
        }

        if (groupBy is not ("model" or "provider" or "day"))
            return ManagementResult<IReadOnlyList<UsageRollupBucket>>.Fail(
                errorType: ManagementErrorType.InvalidRequest,
                message: "groupBy must be 'model', 'provider', or 'day'.");

        return ManagementResultExecutor.TryExecute(
            action: () => _rollupStore.Query(from: from, to: to, bucketWidth: bucketWidth, groupBy: groupBy),
            failureMessage: "Failed to read usage rollups.");
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
            return ManagementResult<IReadOnlyList<RoutingRoiPoint>>.Fail(
                errorType: ManagementErrorType.Unavailable, message: "Routing ROI comparisons are not available.");

        if (to <= from)
            return ManagementResult<IReadOnlyList<RoutingRoiPoint>>.Fail(
                errorType: ManagementErrorType.InvalidRequest, message: "'to' must be after 'from'.");

        return await ManagementResultExecutor.TryExecuteAsync<IReadOnlyList<RoutingRoiPoint>>(action: async () =>
        {
            var rows = await _comparisonStore
                .LoadSinceAsync(since: from, sessionId: sessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return
            [
                .. rows
                    .Where(r => r.ComparedAtUtc < to)
                    .Select(r => new RoutingRoiPoint(
                        ComparedAtUtc: r.ComparedAtUtc,
                        SessionId: r.SessionId,
                        RoutedModel: r.RoutedModel,
                        BaselineModel: r.BaselineModel,
                        ActualCostUsd: r.ActualCostUsd,
                        BaselineEstimatedCostUsd: r.BaselineEstimatedCostUsd,
                        EstimatedNetSavingsUsd: r.EstimatedNetSavingsUsd,
                        IsExploratory: r.IsExploratory))
            ];
        }, failureMessage: "Failed to read routing ROI comparisons.");
    }

    /// <summary>
    /// The Report Card tab's unified spend / grade-mix / score-delta snapshot (GitHub issue #111). Spend
    /// is read from <see cref="IUsageRollupStore"/> grouped by model; grade mix and frozen-baseline score
    /// delta are read from <see cref="ITaxonomyComparisonStore"/>. Neither store is required on its own —
    /// a learning-only database still produces grades and a spend fallback from comparison costs — but
    /// both missing is <see cref="ManagementErrorType.Unavailable"/> rather than an empty card, for the
    /// same reason <see cref="GetRoutingRoiAsync"/> distinguishes "nothing measured" from "measured zero".
    /// </summary>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">Exclusive upper bound; must be after <paramref name="from"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task<ManagementResult<LearningReportCard>> GetLearningReportCardAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (_rollupStore is null && _comparisonStore is null)
            return ManagementResult<LearningReportCard>.Fail(
                errorType: ManagementErrorType.Unavailable,
                message: "The learning report card is not available.");

        if (to <= from)
            return ManagementResult<LearningReportCard>.Fail(
                errorType: ManagementErrorType.InvalidRequest, message: "'to' must be after 'from'.");

        return await ManagementResultExecutor.TryExecuteAsync(action: async () =>
        {
            IReadOnlyList<UsageRollupBucket> buckets = [];
            if (_rollupStore is not null)
            {
                buckets = _rollupStore.Query(from: from, to: to, bucketWidth: "P1D", groupBy: "model");
            }

            IReadOnlyList<TaxonomyComparisonRecord> comparisons = [];
            if (_comparisonStore is not null)
            {
                var rows = await _comparisonStore
                    .LoadSinceAsync(since: from, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                comparisons = [.. rows.Where(row => row.ComparedAtUtc < to)];
            }

            return LearningReportCardAggregator.Aggregate(modelBuckets: buckets, comparisons: comparisons);
        }, failureMessage: "Failed to read the learning report card.");
    }
}