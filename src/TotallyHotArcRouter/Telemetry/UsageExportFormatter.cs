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

    private static void AppendRow(StringBuilder builder, params IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(QuoteIfNeeded(fields[i]));
        }

        builder.Append("\r\n");
    }

    private static string QuoteIfNeeded(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
