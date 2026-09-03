using System.Globalization;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Reads a CodeRouterBench <c>*_results_long.csv</c> table (PLAN.md Phase K) into
/// <see cref="CodeRouterBenchResultRow"/> values. Column order is read from the header row rather than
/// assumed, so the reader tolerates the extra cost/token/latency columns the released tables carry
/// without needing to enumerate them.
/// </summary>
public static class CodeRouterBenchCsvReader
{
    private static readonly IReadOnlyDictionary<string, string> DimensionAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The released CSVs use "algorithm"; the router's own vocabulary (research-doc §4.4) uses
            // "algorithm_design" - see RouterDimension.AlgorithmDesign.
            ["algorithm"] = RouterDimension.AlgorithmDesign
        };

    /// <summary>
    /// Reads every data row from an already-open <paramref name="reader"/>, mapping its <c>dimension</c>
    /// column through <see cref="NormalizeDimension"/> and its <c>model</c> column through
    /// <see cref="ModelNameCanonicalizer.Canonicalize"/>, so callers can key results by
    /// <see cref="RouterDimension"/>'s vocabulary and by a configured <c>ModelName</c> directly - the
    /// released tables spell several models differently from the router's own configuration
    /// (<c>MiniMax-M2.7</c> vs <c>minimax-m2.7</c>, <c>claude-opus-4-6</c> vs <c>claude-opus-4.6</c>).
    /// Deliberately has no file-path overload (docs/router/coderouterbench-sqlite-migration-plan.md's
    /// ground rules): the database is the only source once a sync has run, so this class exists to parse
    /// bytes - typically a freshly downloaded file's, or a stream over a test fixture - never to open a
    /// file on disk itself.
    /// </summary>
    /// <param name="reader">An open reader positioned at the start of the CSV, header row included.</param>
    /// <param name="sourceLabel">A label identifying the source in exception messages (e.g. the originating file name).</param>
    /// <exception cref="FormatException">
    /// Thrown when the header is missing a required <c>task_id</c>, <c>dimension</c>, <c>model</c>, or
    /// <c>score</c> column, or when a data row is short, has a non-numeric score, or has an empty model.
    /// </exception>
    public static IReadOnlyList<CodeRouterBenchResultRow> Read(TextReader reader, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        var headerLine = reader.ReadLine() ?? throw new FormatException($"'{sourceLabel}' has no header row.");
        var columns = SplitLine(headerLine);

        var taskIdIndex = RequireColumn(columns: columns, name: "task_id", csvPath: sourceLabel);
        var dimensionIndex = RequireColumn(columns: columns, name: "dimension", csvPath: sourceLabel);
        var modelIndex = RequireColumn(columns: columns, name: "model", csvPath: sourceLabel);
        var scoreIndex = RequireColumn(columns: columns, name: "score", csvPath: sourceLabel);
        var requiredFieldCount = Math.Max(
            val1: Math.Max(val1: taskIdIndex, val2: dimensionIndex),
            val2: Math.Max(val1: modelIndex, val2: scoreIndex)) + 1;

        List<CodeRouterBenchResultRow> rows = [];
        string? line;
        var rowNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            rowNumber++;

            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = SplitLine(line);
            if (fields.Count < requiredFieldCount)
                throw new FormatException(
                    $"'{sourceLabel}' row {rowNumber} has {fields.Count} columns but requires at least {requiredFieldCount}.");

            var rawScore = fields[scoreIndex];
            if (!double.TryParse(s: rawScore, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture,
                    result: out var score))
                throw new FormatException(
                    $"'{sourceLabel}' has a non-numeric score '{rawScore}' for task '{fields[taskIdIndex]}'.");

            var rawModel = fields[modelIndex];
            if (string.IsNullOrWhiteSpace(rawModel))
                throw new FormatException($"'{sourceLabel}' row {rowNumber} has an empty model.");

            rows.Add(new CodeRouterBenchResultRow(
                TaskId: fields[taskIdIndex],
                Dimension: NormalizeDimension(fields[dimensionIndex]),
                Model: ModelNameCanonicalizer.Canonicalize(rawModel),
                Score: score));
        }

        return rows;
    }

    /// <summary>
    /// Maps a raw CSV <c>dimension</c> value onto <see cref="RouterDimension"/>'s vocabulary via
    /// <see cref="DimensionAliases"/>, or returns it unchanged when it already matches.
    /// </summary>
    public static string NormalizeDimension(string rawDimension)
    {
        return DimensionAliases.TryGetValue(key: rawDimension, value: out var normalized) ? normalized : rawDimension;
    }

    /// <summary>Finds a required column by name (case-insensitive), throwing when absent.</summary>
    /// <param name="columns">The header row's column names.</param>
    /// <param name="name">The required column name to find.</param>
    /// <param name="csvPath">A label identifying the source in the thrown exception's message.</param>
    /// <returns>The column's index.</returns>
    /// <exception cref="FormatException">No column named <paramref name="name"/> is present.</exception>
    private static int RequireColumn(IReadOnlyList<string> columns, string name, string csvPath)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(a: columns[i], b: name, comparisonType: StringComparison.OrdinalIgnoreCase))
                return i;

        throw new FormatException($"'{csvPath}' is missing the required '{name}' column.");
    }

    /// <summary>
    /// Splits one CSV line on commas, honoring double-quoted fields (which may themselves contain
    /// commas) as the released tables' <c>cost_source</c> and task-id columns occasionally do, and
    /// unescaping a doubled <c>""</c> inside a quoted field into a single literal quote. Internal (not
    /// private) so the full-column DB importers under <see cref="BenchmarkSyncService"/> can reuse the
    /// same quoting rules rather than duplicating them.
    /// </summary>
    internal static List<string> SplitLine(string line)
    {
        List<string> fields = [];
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}