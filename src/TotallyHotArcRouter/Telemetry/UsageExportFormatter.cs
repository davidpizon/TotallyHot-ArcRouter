using System.Globalization;
using System.Text;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Formats <see cref="UsageRollupBucket"/> rows for <c>GET /admin/usage/export</c> (§5.12). Pure and
/// platform-neutral (no ASP.NET dependency), so it stays unit-testable without spinning up the endpoint.
/// </summary>
public static class UsageExportFormatter
{
    private static readonly string[] CsvHeader =
    [
        "BucketStartUtc", "BucketWidth", "GroupKey", "Requests", "UnpricedRequests",
        "PromptTokens", "CompletionTokens", "CacheCreationTokens", "CacheReadTokens", "CostUsd",
    ];

    /// <summary>
    /// Renders <paramref name="buckets"/> as RFC 4180 CSV: a header row, then one row per bucket, in the
    /// order given (already the caller's - <c>UsageRollupStore.Query</c>'s - own order; not re-sorted
    /// here). Every field is quoted only when it needs to be (contains a comma, quote, or newline), per
    /// RFC 4180 - not unconditionally, so a plain export stays easy to eyeball.
    /// </summary>
    public static string ToCsv(IReadOnlyList<UsageRollupBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        var builder = new StringBuilder();
        AppendRow(builder, CsvHeader);

        foreach (var bucket in buckets)
        {
            AppendRow(
                builder,
                bucket.BucketStartUtc.ToString("O", CultureInfo.InvariantCulture),
                bucket.BucketWidth,
                bucket.GroupKey,
                bucket.Requests.ToString(CultureInfo.InvariantCulture),
                bucket.UnpricedRequests.ToString(CultureInfo.InvariantCulture),
                bucket.PromptTokens.ToString(CultureInfo.InvariantCulture),
                bucket.CompletionTokens.ToString(CultureInfo.InvariantCulture),
                bucket.CacheCreationTokens.ToString(CultureInfo.InvariantCulture),
                bucket.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
                bucket.CostUsd.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Appends one CSV row (each field quoted only if needed, per RFC 4180) followed by a CRLF line terminator.</summary>
    /// <param name="builder">The buffer to append to.</param>
    /// <param name="fields">The row's field values, in column order.</param>
    private static void AppendRow(StringBuilder builder, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(QuoteIfNeeded(fields[i]));
        }

        builder.Append("\r\n");
    }

    // Spreadsheet formula injection (OWASP CSV Injection): GroupKey ultimately comes from request/model/
    // provider strings, so a crafted value starting with one of these characters would execute as a
    // formula if an operator opens the export in Excel/Sheets. Prefixing with an apostrophe is the
    // standard mitigation - it forces the cell to render as text without altering the field's real value
    // for any other consumer (a CSV parser sees the literal leading apostrophe, same as any other char).
    private static readonly char[] FormulaTriggerChars = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Quotes <paramref name="field"/> per RFC 4180 if it contains a comma, quote, or newline, and
    /// prefixes it with an apostrophe first if it could otherwise be interpreted as a spreadsheet
    /// formula (see the CSV-injection remarks above <see cref="FormulaTriggerChars"/>).
    /// </summary>
    /// <param name="field">The raw field value.</param>
    /// <returns>The field value, quoted and/or formula-escaped as needed for safe CSV output.</returns>
    private static string QuoteIfNeeded(string field)
    {
        // Spreadsheet apps trim leading spaces before deciding whether a cell's content is a formula, so
        // " =1+1" is just as dangerous as "=1+1" - check the first non-space character, not literally
        // field[0], while still catching a field that leads with a trigger char directly (including tab/CR,
        // which are themselves in FormulaTriggerChars rather than being skipped over like plain spaces).
        var firstNonSpace = 0;
        while (firstNonSpace < field.Length && field[firstNonSpace] == ' ')
        {
            firstNonSpace++;
        }

        if (firstNonSpace < field.Length && Array.IndexOf(FormulaTriggerChars, field[firstNonSpace]) >= 0)
        {
            field = "'" + field;
        }

        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
