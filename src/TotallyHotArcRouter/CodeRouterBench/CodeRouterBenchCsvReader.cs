using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Sandbox;

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
            ["algorithm"] = RouterDimension.AlgorithmDesign,
        };

    /// <summary>
    /// Reads every data row of <paramref name="csvPath"/>, mapping its <c>dimension</c> column through
    /// <see cref="NormalizeDimension"/> and its <c>model</c> column through
    /// <see cref="ModelNameCanonicalizer.Canonicalize"/>, so callers can key results by
    /// <see cref="RouterDimension"/>'s vocabulary and by a configured <c>ModelName</c> directly - the
    /// released tables spell several models differently from the router's own configuration
    /// (<c>MiniMax-M2.7</c> vs <c>minimax-m2.7</c>, <c>claude-opus-4-6</c> vs <c>claude-opus-4.6</c>).
    /// </summary>
    /// <param name="csvPath">Path to a <c>*_results_long.csv</c> file.</param>
    /// <exception cref="FormatException">
    /// Thrown when the header is missing a required <c>task_id</c>, <c>dimension</c>, <c>model</c>, or
    /// <c>score</c> column, or when a data row is short, has a non-numeric score, or has an empty model.
    /// </exception>
    public static IReadOnlyList<CodeRouterBenchResultRow> Read(string csvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);

        using var reader = new StreamReader(csvPath);

        var headerLine = reader.ReadLine() ?? throw new FormatException($"'{csvPath}' has no header row.");
        var columns = SplitLine(headerLine);

        var taskIdIndex = RequireColumn(columns, "task_id", csvPath);
        var dimensionIndex = RequireColumn(columns, "dimension", csvPath);
        var modelIndex = RequireColumn(columns, "model", csvPath);
        var scoreIndex = RequireColumn(columns, "score", csvPath);
        var requiredFieldCount = Math.Max(
            Math.Max(taskIdIndex, dimensionIndex),
            Math.Max(modelIndex, scoreIndex)) + 1;

        List<CodeRouterBenchResultRow> rows = [];
        string? line;
        var rowNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            rowNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = SplitLine(line);
            if (fields.Count < requiredFieldCount)
            {
                throw new FormatException(
                    $"'{csvPath}' row {rowNumber} has {fields.Count} columns but requires at least {requiredFieldCount}.");
            }

            var rawScore = fields[scoreIndex];
            if (!double.TryParse(rawScore, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score))
            {
                throw new FormatException($"'{csvPath}' has a non-numeric score '{rawScore}' for task '{fields[taskIdIndex]}'.");
            }

            var rawModel = fields[modelIndex];
            if (string.IsNullOrWhiteSpace(rawModel))
            {
                throw new FormatException($"'{csvPath}' row {rowNumber} has an empty model.");
            }

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
    public static string NormalizeDimension(string rawDimension) =>
        DimensionAliases.TryGetValue(rawDimension, out var normalized) ? normalized : rawDimension;

    private static int RequireColumn(IReadOnlyList<string> columns, string name, string csvPath)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new FormatException($"'{csvPath}' is missing the required '{name}' column.");
    }

    /// <summary>
    /// Splits one CSV line on commas, honoring double-quoted fields (which may themselves contain
    /// commas) as the released tables' <c>cost_source</c> and task-id columns occasionally do, and
    /// unescaping a doubled <c>""</c> inside a quoted field into a single literal quote.
    /// </summary>
    private static List<string> SplitLine(string line)
    {
        List<string> fields = [];
        var current = new System.Text.StringBuilder();
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
