namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The rank-correlation primitives shared by <see cref="GraderReliabilityAnalyzer"/> (Phase Q4) and
/// <see cref="JudgeCalibrationAnalyzer"/> (Phase G2). Extracted when G2 needed the identical Spearman
/// computation Q4 already had: two independent copies of a statistic would let one analyzer's
/// tie-handling or zero-variance convention drift from the other's, and the two reports are read side by
/// side, so a silent divergence would be read as a difference in the data rather than in the arithmetic.
/// </summary>
/// <remarks>
/// Deliberately Spearman rather than Pearson everywhere it is used. Both callers correlate bounded
/// <c>[0,1]</c> opinions that need not vary linearly with one another - G-Eval's probability-weighted
/// score against a static analyzer's weighted blend, or one rubric grader against another - and a rank
/// correlation tolerates that where a linear one would understate the agreement.
/// </remarks>
internal static class RankCorrelation
{
    /// <summary>
    /// Computes the Spearman rank correlation between two equal-length samples: ranks each sample
    /// (averaging ranks across ties) and returns the Pearson correlation of the two rank sequences, or
    /// <see langword="null"/> when one sample has zero variance (see <see cref="Pearson"/>).
    /// </summary>
    /// <param name="x">The first sample.</param>
    /// <param name="y">The second sample, of the same length as <paramref name="x"/>.</param>
    /// <returns>The correlation in <c>[-1,1]</c>, or <see langword="null"/> when it is undefined.</returns>
    internal static double? Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var ranksX = Rank(x);
        var ranksY = Rank(y);
        return Pearson(ranksX, ranksY);
    }

    /// <summary>Ranks a sample in ascending order, giving tied values the average of the ranks they span.</summary>
    /// <param name="values">The sample to rank.</param>
    /// <returns>The rank of each value, in the input's original order.</returns>
    internal static double[] Rank(IReadOnlyList<double> values)
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
    /// <param name="x">The first sample.</param>
    /// <param name="y">The second sample, of the same length as <paramref name="x"/>.</param>
    /// <returns>The correlation in <c>[-1,1]</c>, or <see langword="null"/> when it is undefined.</returns>
    internal static double? Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
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

    /// <summary>
    /// The population standard deviation of a sample, or <see langword="null"/> for an empty one. The
    /// spread statistic G-Eval's score-collapse check reads: a judge that answers "3" to everything has a
    /// standard deviation at or near zero while its mean stays perfectly plausible
    /// (docs/research/2303.16634v3.md, "score collapse").
    /// </summary>
    /// <param name="values">The sample to measure.</param>
    /// <returns>The population standard deviation, or <see langword="null"/> when <paramref name="values"/> is empty.</returns>
    internal static double? StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;

        var mean = values.Average();
        var sumOfSquares = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sumOfSquares / values.Count);
    }

    /// <summary>
    /// The proportion of a sample taken by its single most common value, in <c>(0,1]</c>, or
    /// <see langword="null"/> for an empty sample. The second half of the score-collapse check, and the one
    /// that catches what a standard deviation alone misses: a judge emitting 3, 3, 3, 3, 5 has a
    /// respectable spread supplied entirely by one outlier, while 80% of its output is a single digit.
    /// Values are bucketed at three decimal places, since a probability-weighted score is continuous and
    /// exact equality would never register a mode at all.
    /// </summary>
    /// <param name="values">The sample to measure.</param>
    /// <returns>The modal share, or <see langword="null"/> when <paramref name="values"/> is empty.</returns>
    internal static double? ModalShare(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;

        var largestBucket = values
            .GroupBy(value => Math.Round(value, digits: 3))
            .Max(bucket => bucket.Count());
        return (double)largestBucket / values.Count;
    }

    /// <summary>The mean absolute difference between two equal-length samples, paired element-wise.</summary>
    /// <param name="x">The first sample.</param>
    /// <param name="y">The second sample, of the same length as <paramref name="x"/>.</param>
    /// <returns>The mean absolute difference, or <see langword="null"/> when the samples are empty.</returns>
    internal static double? MeanAbsoluteDifference(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count == 0) return null;

        var total = 0.0;
        for (var i = 0; i < x.Count; i++) total += Math.Abs(x[i] - y[i]);
        return total / x.Count;
    }
}
