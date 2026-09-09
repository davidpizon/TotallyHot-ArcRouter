using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The <see cref="IGraderReliabilityAnalyzer"/> implementation: reads every <c>grader_scores</c> row once,
/// groups by dimension, and computes inter-grader agreement, verbosity skew, and self-preference skew per
/// dimension (docs/router/grader-reliability-plan.md, Phase Q4).
/// </summary>
public sealed class GraderReliabilityAnalyzer : IGraderReliabilityAnalyzer
{
    private readonly IGraderScoreStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="GraderReliabilityAnalyzer"/> class.</summary>
    /// <param name="store">Supplies every <c>grader_scores</c> row to analyze.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp <see cref="GraderReliabilityReport.GeneratedAtUtc"/>; defaults to
    /// <see cref="TimeProvider.System"/>. Overridable for deterministic tests.
    /// </param>
    public GraderReliabilityAnalyzer(IGraderScoreStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public int MinimumSampleSize => 30;

    /// <inheritdoc/>
    public async Task<GraderReliabilityReport> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var dimensions = rows
            .GroupBy(keySelector: r => r.Dimension, comparer: StringComparer.OrdinalIgnoreCase)
            .Select(AnalyzeDimension)
            .OrderBy(d => d.Dimension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GraderReliabilityReport(
            Dimensions: dimensions,
            GeneratedAtUtc: _timeProvider.GetUtcNow(),
            TotalRowsAnalyzed: rows.Count);
    }

    /// <summary>Computes one dimension's full slice of the report from its rows.</summary>
    private GraderReliabilityDimensionReport AnalyzeDimension(IGrouping<string, GraderScoreRecord> dimensionRows)
    {
        var rows = dimensionRows.ToList();
        var graderKeys = rows.Select(r => r.GraderKey).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        return new GraderReliabilityDimensionReport(
            Dimension: dimensionRows.Key,
            PairAgreements: ComputePairAgreements(rows: rows, graderKeys: graderKeys),
            VerbositySkews: ComputeVerbositySkews(rows: rows, graderKeys: graderKeys),
            SelfPreferenceSkews: ComputeSelfPreferenceSkews(rows: rows, graderKeys: graderKeys));
    }

    /// <summary>
    /// For every unordered pair of grader keys, correlates their scores over the requests both graders
    /// actually scored (matched by correlation id) - never zero-filling a grader that did not score a given
    /// request.
    /// </summary>
    /// <remarks>
    /// Two defensive steps before pairing, neither of which the write path is expected to trigger today
    /// (<see cref="IQualityScoreAggregator"/> guarantees exactly one final write per request) but
    /// both of which would otherwise crash a re-run against once-corrupted or hand-edited data:
    /// <list type="bullet">
    /// <item>Rows with an empty <see cref="GraderScoreRecord.CorrelationId"/> are excluded from the join -
    /// they cannot be paired across graders anyway (nothing ties one such row to another), and grouping them
    /// together by their shared empty key would incorrectly treat unrelated requests as the same one.</item>
    /// <item>Within a correlation id, more than one row for the same grader key collapses to the most
    /// recent by <see cref="GraderScoreRecord.CreatedAtUtc"/> - <c>ToDictionary</c> throws on a duplicate
    /// key, and a duplicate write (a retry, a manual edit) is data noise to collapse, not a reason to fail
    /// the whole report.</item>
    /// </list>
    /// The write path is not expected to trigger either case today - the quality-aggregator's join
    /// guarantees exactly one final write per request - but a re-run against once-corrupted or
    /// hand-edited data should degrade gracefully rather than throw.
    /// </remarks>
    private List<GraderPairAgreement> ComputePairAgreements(IReadOnlyList<GraderScoreRecord> rows,
        IReadOnlyList<string> graderKeys)
    {
        var byCorrelationThenGrader = rows
            .Where(r => !string.IsNullOrEmpty(r.CorrelationId))
            .GroupBy(keySelector: r => r.CorrelationId, comparer: StringComparer.Ordinal)
            .Select(g => g
                .GroupBy(keySelector: r => r.GraderKey, comparer: StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    keySelector: gg => gg.Key,
                    elementSelector: gg => gg.OrderByDescending(r => r.CreatedAtUtc).First().Score,
                    comparer: StringComparer.OrdinalIgnoreCase))
            .ToList();

        List<GraderPairAgreement> agreements = [];
        for (var i = 0; i < graderKeys.Count; i++)
        for (var j = i + 1; j < graderKeys.Count; j++)
        {
            var graderA = graderKeys[i];
            var graderB = graderKeys[j];

            List<double> scoresA = [];
            List<double> scoresB = [];
            foreach (var perRequest in byCorrelationThenGrader)
            {
                if (!perRequest.TryGetValue(key: graderA, value: out var scoreA)) continue;
                if (!perRequest.TryGetValue(key: graderB, value: out var scoreB)) continue;
                scoresA.Add(scoreA);
                scoresB.Add(scoreB);
            }

            agreements.Add(new GraderPairAgreement(
                GraderA: graderA,
                GraderB: graderB,
                Correlation: scoresA.Count >= MinimumSampleSize ? SpearmanCorrelation(scoresA, scoresB) : null,
                SampleSize: scoresA.Count));
        }

        return agreements;
    }

    /// <summary>Correlates each grader's score against the response length recorded on the same row.</summary>
    private List<GraderVerbositySkew> ComputeVerbositySkews(IReadOnlyList<GraderScoreRecord> rows,
        IReadOnlyList<string> graderKeys)
    {
        List<GraderVerbositySkew> skews = [];
        foreach (var graderKey in graderKeys)
        {
            var paired = rows
                .Where(r => string.Equals(r.GraderKey, graderKey, StringComparison.OrdinalIgnoreCase) &&
                            r.ResponseLengthChars is not null)
                .Select(r => (r.Score, Length: (double)r.ResponseLengthChars!.Value))
                .ToList();

            skews.Add(new GraderVerbositySkew(
                GraderKey: graderKey,
                Correlation: paired.Count >= MinimumSampleSize
                    ? SpearmanCorrelation([.. paired.Select(p => p.Score)], [.. paired.Select(p => p.Length)])
                    : null,
                SampleSize: paired.Count));
        }

        return skews;
    }

    /// <summary>
    /// For every grader key with at least one recorded backbone, compares its mean score when the graded
    /// candidate matches that backbone against its mean score otherwise.
    /// </summary>
    private static List<GraderSelfPreferenceSkew> ComputeSelfPreferenceSkews(IReadOnlyList<GraderScoreRecord> rows,
        IReadOnlyList<string> graderKeys)
    {
        List<GraderSelfPreferenceSkew> skews = [];
        foreach (var graderKey in graderKeys)
        {
            var graderRows = rows
                .Where(r => string.Equals(r.GraderKey, graderKey, StringComparison.OrdinalIgnoreCase) &&
                            r.GraderBackboneModel is not null)
                .ToList();
            if (graderRows.Count == 0) continue;

            var ownModelScores = graderRows
                .Where(r => string.Equals(r.Model, r.GraderBackboneModel, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Score).ToList();
            var otherModelScores = graderRows
                .Where(r => !string.Equals(r.Model, r.GraderBackboneModel, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Score).ToList();

            skews.Add(new GraderSelfPreferenceSkew(
                GraderKey: graderKey,
                MeanScoreDelta: ownModelScores.Count > 0 && otherModelScores.Count > 0
                    ? ownModelScores.Average() - otherModelScores.Average()
                    : null,
                OwnModelSampleSize: ownModelScores.Count,
                OtherModelSampleSize: otherModelScores.Count));
        }

        return skews;
    }

    /// <summary>
    /// Computes the Spearman rank correlation between two equal-length samples: ranks each sample
    /// (averaging ranks across ties) and returns the Pearson correlation of the two rank sequences, or
    /// <see langword="null"/> when one sample has zero variance (see <see cref="PearsonCorrelation"/>).
    /// </summary>
    private static double? SpearmanCorrelation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var ranksX = Rank(x);
        var ranksY = Rank(y);
        return PearsonCorrelation(ranksX, ranksY);
    }

    /// <summary>Ranks a sample in ascending order, giving tied values the average of the ranks they span.</summary>
    private static double[] Rank(IReadOnlyList<double> values)
    {
        var indexed = values.Select((value, index) => (value, index)).OrderBy(t => t.value).ToArray();
        var ranks = new double[values.Count];

        var i = 0;
        while (i < indexed.Length)
        {
            var j = i;
            // .Equals(), not ==: this is an intentional exact-tie check over already-computed scores
            // (some of which are legitimately identical, e.g. a clamped 0.0/1.0 boundary), not a
            // should-be-approximate comparison of independently computed floating-point results.
            while (j + 1 < indexed.Length && indexed[j + 1].value.Equals(indexed[i].value)) j++;

            // 1-based ranks i+1..j+1, averaged across the tied run.
            var averageRank = ((i + 1) + (j + 1)) / 2.0;
            for (var k = i; k <= j; k++) ranks[indexed[k].index] = averageRank;

            i = j + 1;
        }

        return ranks;
    }

    /// <summary>
    /// Computes the Pearson correlation coefficient between two equal-length samples, or
    /// <see langword="null"/> when either sample has zero variance (every value identical). Zero variance
    /// makes the coefficient a literal 0/0 - mathematically undefined, not "no correlation" - so it is
    /// suppressed the same way a too-small sample size already is, rather than reported as a misleadingly
    /// precise 0.0.
    /// </summary>
    private static double? PearsonCorrelation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        var meanX = x.Average();
        var meanY = y.Average();

        var covariance = 0.0;
        var varianceX = 0.0;
        var varianceY = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX == 0.0 || varianceY == 0.0) return null;
        return covariance / Math.Sqrt(varianceX * varianceY);
    }
}
