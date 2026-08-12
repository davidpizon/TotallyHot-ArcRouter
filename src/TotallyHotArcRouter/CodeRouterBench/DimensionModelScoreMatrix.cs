using TotallyHot.ArcRouter.Models;

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
    /// <remarks>
    /// Model ids are keyed through <see cref="ModelNameCanonicalizer.Canonicalize"/> here as well as in
    /// <see cref="AverageScore"/>. Canonicalizing on ingest rather than trusting the caller keeps the
    /// matrix self-consistent whichever spelling its rows arrived in, which is what lets a Phase L
    /// <c>dim_best</c> lookup pass a configured <c>ModelName</c> and hit rows the dataset stored under
    /// its own spelling.
    /// </remarks>
    public static DimensionModelScoreMatrix FromRows(IEnumerable<CodeRouterBenchResultRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Dictionary<(string, string), (double Sum, int Count)> accumulators = [];
        foreach (var row in rows)
        {
            var key = (row.Dimension, ModelNameCanonicalizer.Canonicalize(row.Model));
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
    /// <param name="dimension">A <see cref="Sandbox.RouterDimension"/> key, matched verbatim.</param>
    /// <param name="model">
    /// Any spelling of a model id - a configured <c>ModelName</c> or the dataset's own - matched through
    /// <see cref="ModelNameCanonicalizer.Canonicalize"/>.
    /// </param>
    public double? AverageScore(string dimension, string model) =>
        _averages.TryGetValue((dimension, ModelNameCanonicalizer.Canonicalize(model)), out var average) ? average : null;
}
