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
    /// <param name="rows">The result rows to aggregate.</param>
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
    /// Builds the matrix by averaging <c>score</c> over every <c>benchmark_id_results</c> row in
    /// <paramref name="split"/>, sharing the same (dimension, model) pair
    /// (docs/router/coderouterbench-sqlite-migration-plan.md Phase 6). The database-backed replacement
    /// for <see cref="FromRows"/>: once the corpus lives in <see cref="BenchmarkDatabase"/>, this is what
    /// Phase L's <c>dim_best</c> voter builds its matrix from.
    /// </summary>
    /// <param name="database">The corpus database to read <c>benchmark_id_results</c> from.</param>
    /// <param name="split">The <c>split</c> value to filter on, e.g. <c>"probing"</c> or <c>"id_test"</c>.</param>
    /// <remarks>
    /// Model ids are re-canonicalized on read even though <c>BenchmarkIdResultsCsvImporter</c> already
    /// canonicalizes on import, for the same reason <see cref="FromRows"/> does: a defensive no-op against
    /// whichever spelling actually ended up in the table, rather than a second place this class trusts the
    /// data to already be clean.
    /// </remarks>
    public static DimensionModelScoreMatrix FromDatabase(BenchmarkDatabase database, string split)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dimension, model, score FROM benchmark_id_results WHERE split = $split;";
        command.Parameters.AddWithValue("$split", split);

        Dictionary<(string, string), (double Sum, int Count)> accumulators = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), ModelNameCanonicalizer.Canonicalize(reader.GetString(1)));
            var score = reader.GetDouble(2);
            var (sum, count) = accumulators.TryGetValue(key, out var existing) ? existing : (0.0, 0);
            accumulators[key] = (sum + score, count + 1);
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
