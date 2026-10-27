using System.Globalization;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// One read of the grader-epoch chart (issue #304 phase 9). Epoch rows and transcript rows stay in
/// their own files. This snapshot does not join them in SQL, and it carries no question text.
/// </summary>
/// <param name="Points">Token-efficiency buckets. A gap omits both ratio strings.</param>
/// <param name="Markers">Epoch rows inside the window, oldest first.</param>
/// <param name="Incomplete">Incomplete receipt counts for the same window.</param>
public sealed record GraderEpochChartSnapshot(
    IReadOnlyList<EfficiencyBucket> Points,
    IReadOnlyList<GraderEpochEventRow> Markers,
    IncompleteGradeCounts Incomplete);

/// <summary>
/// Reads <c>grader_epoch_events</c>, <c>request_transcripts</c>, and incomplete <c>grade_receipts</c>
/// on their own connections and places the first two on one time axis in C# (issue #304 phase 9).
/// It does not read <c>usage_rollup</c>. A null database returns unavailable rather than an empty success.
/// </summary>
public sealed class GraderEpochChartQuery
{
    private readonly IGraderEpochEventStore? _epochEvents;
    private readonly TranscriptDatabase? _transcripts;
    private readonly RouterMemoryDatabase? _gradeReceipts;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraderEpochChartQuery"/> class. Any null dependency
    /// makes <see cref="Get"/> return unavailable, which is also what a missing inner-host registration does.
    /// </summary>
    /// <param name="epochEvents">Append-only epoch history. <see cref="IGraderEpochEventStore.ListForPlot"/> is not given a time filter.</param>
    /// <param name="transcripts">The transcript file. Null when the inner host was not handed one.</param>
    /// <param name="gradeReceipts">The router-memory file that holds receipts and epoch rows.</param>
    /// <param name="logger">Warns when an epoch timestamp does not parse. May be null in a hand-built test.</param>
    public GraderEpochChartQuery(
        IGraderEpochEventStore? epochEvents,
        TranscriptDatabase? transcripts,
        RouterMemoryDatabase? gradeReceipts,
        ILogger? logger = null)
    {
        _epochEvents = epochEvents;
        _transcripts = transcripts;
        _gradeReceipts = gradeReceipts;
        _logger = logger;
    }

    /// <summary>
    /// Reads the three lists for <c>[from, to)</c>. <paramref name="width"/> is <c>hour</c> or <c>day</c>.
    /// The ratio is computed here. The page does not divide.
    /// </summary>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">Exclusive upper bound. Must be after <paramref name="from"/>.</param>
    /// <param name="width"><c>hour</c> or <c>day</c>.</param>
    /// <returns>The snapshot, or unavailable / invalid-request when the read cannot be done.</returns>
    public ManagementResult<GraderEpochChartSnapshot> Get(DateTimeOffset from, DateTimeOffset to, string width)
    {
        if (_epochEvents is null || _transcripts is null || _gradeReceipts is null)
            return ManagementResult<GraderEpochChartSnapshot>.Fail(
                errorType: ManagementErrorType.Unavailable,
                message: "The grader epoch chart is not available.");

        if (to <= from)
            return ManagementResult<GraderEpochChartSnapshot>.Fail(
                errorType: ManagementErrorType.InvalidRequest, message: "'to' must be after 'from'.");

        if (width is not ("hour" or "day"))
            return ManagementResult<GraderEpochChartSnapshot>.Fail(
                errorType: ManagementErrorType.InvalidRequest, message: "width must be 'hour' or 'day'.");

        return ManagementResultExecutor.TryExecute(action: () =>
        {
            var markers = Markers(from: from, to: to);
            var transcripts = new TranscriptTokenCostReader(database: _transcripts, logger: _logger).List(from: from, to: to);
            var points = GraderEpochEfficiency.Bucket(points: transcripts, width: width);
            var incomplete = new IncompleteGradeCountQuery(_gradeReceipts).Count(from: from, to: to);
            return new GraderEpochChartSnapshot(Points: points, Markers: markers, Incomplete: incomplete);
        }, failureMessage: "Failed to read the grader epoch chart.");
    }

    /// <summary>
    /// Keeps <see cref="IGraderEpochEventStore.ListForPlot"/> rows inside the window. A row that does not
    /// parse is skipped and logged. It is not plotted at time zero. Order stays <c>at_utc</c>, then id.
    /// </summary>
    private List<GraderEpochEventRow> Markers(DateTimeOffset from, DateTimeOffset to)
    {
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var kept = new List<GraderEpochEventRow>();
        foreach (var row in _epochEvents!.ListForPlot())
        {
            if (!DateTimeOffset.TryParse(input: row.AtUtc, formatProvider: CultureInfo.InvariantCulture,
                    styles: DateTimeStyles.RoundtripKind, result: out var at))
            {
                _logger?.LogWarning("Skipping grader epoch event {EventId} because at_utc did not parse.", row.Id);
                continue;
            }

            var atUtc = at.ToUniversalTime();
            if (atUtc < fromUtc || atUtc >= toUtc)
                continue;
            kept.Add(row);
        }

        return kept;
    }
}
