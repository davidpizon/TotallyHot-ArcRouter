namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// A dimension x model average-score matrix built from a CodeRouterBench results table (PLAN.md
/// Phase K) - the shape research-doc Table 10/11 publish, and the backing store Phase L's
/// <c>dim_best</c> voter reads from once it is wired in.
/// </summary>
public sealed class DimensionModelScoreMatrix
{
    private readonly IReadOnlyDictionary<(string Dimension, string Model), double> _averages;

    private DimensionModelScoreMatrix(IReadOnlyDictionary<(string Dimension, string Model), double> averages)
    {
        _averages = averages;
    }

    /// <summary>
    /// Builds the matrix by averaging <see cref="CodeRouterBenchResultRow.Score"/> over every row
    /// sharing the same (dimension, model) pair.
    /// </summary>
    /// <param name="rows">The result rows to aggregate, typically from <see cref="CodeRouterBenchCsvReader.Read"/>.</param>
    public static DimensionModelScoreMatrix FromRows(IEnumerable<CodeRouterBenchResultRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Dictionary<(string, string), (double Sum, int Count)> accumulators = [];
        foreach (var row in rows)
        {
            var key = (row.Dimension, row.Model);
            var (sum, count) = accumulators.TryGetValue(key, out var existing) ? existing : (0.0, 0);
            accumulators[key] = (sum + row.Score, count + 1);
        }

        var averages = accumulators.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Sum / kvp.Value.Count);
        return new DimensionModelScoreMatrix(averages);
    }

    /// <summary>
    /// Gets the average score for <paramref name="dimension"/> x <paramref name="model"/>, or
    /// <see langword="null"/> when no row in the source data had that pair.
    /// </summary>
    public double? AverageScore(string dimension, string model) =>
        _averages.TryGetValue((dimension, model), out var average) ? average : null;
}
