using System.Globalization;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Header-column lookup and optional-field parsing shared by
/// <see cref="BenchmarkIdResultsCsvImporter"/> and <see cref="BenchmarkOodResultsCsvImporter"/>, both of
/// which read column order from the header row rather than assuming it, so the released tables' extra
/// columns never need to be enumerated up front.
/// </summary>
internal static class BenchmarkCsvColumns
{
    /// <summary>Finds a required column by name (case-insensitive), throwing when absent.</summary>
    public static int RequireColumn(IReadOnlyList<string> columns, string name)
    {
        var index = FindColumn(columns, name);
        return index >= 0 ? index : throw new FormatException($"The CSV is missing the required '{name}' column.");
    }

    /// <summary>Finds a column by name (case-insensitive), returning -1 when absent.</summary>
    public static int FindColumn(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads a field as a string, returning <see langword="null"/> when the column is absent from the
    /// header or the field is blank - absent stays absent rather than becoming an empty-string row.
    /// </summary>
    public static string? ReadOptionalString(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count && !string.IsNullOrWhiteSpace(fields[index]) ? fields[index] : null;

    /// <summary>Reads a field as a decimal, returning <see langword="null"/> when absent or unparsable.</summary>
    public static decimal? ReadOptionalDecimal(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count &&
        decimal.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Reads a field as an integer, returning <see langword="null"/> when absent or unparsable.</summary>
    public static long? ReadOptionalInt(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count &&
        long.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// Reads a field as a 0/1 boolean flag (the OOD table's <c>resolved</c>/<c>apply_ok</c>/<c>graded</c>
    /// columns), returning <see langword="null"/> when absent or unparsable. Accepts <c>0</c>/<c>1</c> and
    /// <c>true</c>/<c>false</c>.
    /// </summary>
    public static long? ReadOptionalBoolAsInt(IReadOnlyList<string> fields, int index)
    {
        var raw = ReadOptionalString(fields, index);
        if (raw is null)
        {
            return null;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        if (bool.TryParse(raw, out var boolean))
        {
            return boolean ? 1 : 0;
        }

        return null;
    }
}
