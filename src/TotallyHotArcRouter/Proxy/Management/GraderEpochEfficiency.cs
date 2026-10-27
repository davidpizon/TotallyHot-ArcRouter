using System.Globalization;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// One token-efficiency bucket. The two strings are omitted together when the bucket has no known row.
/// This is a gap, not a point at 0. <see cref="KnownRows"/> and <see cref="SkippedRows"/> are real counts.
/// </summary>
/// <param name="BucketStartUtc">The UTC hour or UTC midnight this bucket starts at.</param>
/// <param name="TokensPerDollar">Token sum divided by cost sum, or null when nothing known landed here.</param>
/// <param name="DollarsPerThousandTokens">Cost sum divided by token sum, times 1,000, or null with <paramref name="TokensPerDollar"/>.</param>
/// <param name="KnownRows">Rows that had both token counts, a cost greater than 0, and a token sum greater than 0.</param>
/// <param name="SkippedRows">Rows in this bucket that were left out of both sums. A null is not added as 0.</param>
public sealed record EfficiencyBucket(
    DateTimeOffset BucketStartUtc,
    string? TokensPerDollar,
    string? DollarsPerThousandTokens,
    int KnownRows,
    int SkippedRows);

/// <summary>
/// Buckets transcript token and cost rows into the two token-efficiency views (issue #304 phase 9).
/// This is the only place that divides. The page chooses which string to plot and does not divide again.
/// </summary>
public static class GraderEpochEfficiency
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Buckets <paramref name="points"/> by UTC hour or UTC day. A row is known only when both token counts
    /// and the cost are present, the cost is greater than 0, and the token sum is greater than 0. Every
    /// other row is skipped in both views. Empty hours or days are inserted only between the first and last
    /// known bucket, so All time does not start at year 1. A skipped-only bucket outside that span is still returned.
    /// </summary>
    /// <param name="points">Transcript rows already limited to the chart window.</param>
    /// <param name="width"><c>hour</c> or <c>day</c>.</param>
    /// <returns>Buckets in time order. A bucket with no known row omits both strings.</returns>
    public static IReadOnlyList<EfficiencyBucket> Bucket(IReadOnlyList<TranscriptTokenCostPoint> points, string width)
    {
        ArgumentNullException.ThrowIfNull(points);
        var hour = width == "hour";
        var buckets = new SortedDictionary<DateTimeOffset, Accumulator>();
        foreach (var point in points)
        {
            var start = Start(instant: point.CreatedAtUtc, hour: hour);
            if (!buckets.TryGetValue(key: start, value: out var accumulator))
            {
                accumulator = new Accumulator();
                buckets[start] = accumulator;
            }

            if (TryKnown(point: point, tokens: out var tokens, cost: out var cost))
            {
                accumulator.Known++;
                accumulator.TokenSum += tokens;
                accumulator.CostSum += cost;
            }
            else
            {
                accumulator.Skipped++;
            }
        }

        FillInteriorGaps(buckets: buckets, hour: hour);
        return buckets.Select(pair => pair.Value.ToBucket(pair.Key)).ToList();
    }

    /// <summary>Inserts a gap slot for every missing step strictly between the first and last known bucket.</summary>
    private static void FillInteriorGaps(SortedDictionary<DateTimeOffset, Accumulator> buckets, bool hour)
    {
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        foreach (var pair in buckets)
        {
            if (pair.Value.Known == 0)
                continue;
            first ??= pair.Key;
            last = pair.Key;
        }

        if (first is null || last is null)
            return;

        for (var cursor = first.Value; cursor < last.Value;)
        {
            cursor = hour ? cursor.AddHours(1) : cursor.AddDays(1);
            if (cursor > last.Value)
                break;
            if (!buckets.ContainsKey(cursor))
                buckets[cursor] = new Accumulator();
        }
    }

    /// <summary>The UTC hour or UTC midnight that contains <paramref name="instant"/>.</summary>
    private static DateTimeOffset Start(DateTimeOffset instant, bool hour)
    {
        var utc = instant.ToUniversalTime();
        return hour
            ? new DateTimeOffset(year: utc.Year, month: utc.Month, day: utc.Day, hour: utc.Hour, minute: 0, second: 0, offset: TimeSpan.Zero)
            : new DateTimeOffset(year: utc.Year, month: utc.Month, day: utc.Day, hour: 0, minute: 0, second: 0, offset: TimeSpan.Zero);
    }

    /// <summary>
    /// A row is known when both token counts and the cost have values, the cost is greater than 0,
    /// and the token sum is greater than 0. A null, a zero, and a negative are skipped. Skipping does not add 0.
    /// </summary>
    private static bool TryKnown(TranscriptTokenCostPoint point, out long tokens, out decimal cost)
    {
        tokens = 0;
        cost = 0m;
        if (point.InputTokens is not int input || point.OutputTokens is not int output || point.Cost is not decimal stored)
            return false;

        var sum = (long)input + output;
        if (stored <= 0m || sum <= 0)
            return false;

        tokens = sum;
        cost = stored;
        return true;
    }

    /// <summary>Formats a ratio so the string parses back to the same decimal. No currency symbol.</summary>
    private static string Format(decimal value)
    {
        return value.ToString(format: "G29", provider: Inv);
    }

    private sealed class Accumulator
    {
        public int Known { get; set; }

        public int Skipped { get; set; }

        public long TokenSum { get; set; }

        public decimal CostSum { get; set; }

        public EfficiencyBucket ToBucket(DateTimeOffset start)
        {
            if (Known == 0)
                return new EfficiencyBucket(BucketStartUtc: start, null, null, KnownRows: 0, SkippedRows: Skipped);

            var tokensPerDollar = Format(TokenSum / CostSum);
            var dollarsPerThousand = Format(CostSum / TokenSum * 1000m);
            return new EfficiencyBucket(
                BucketStartUtc: start,
                TokensPerDollar: tokensPerDollar,
                DollarsPerThousandTokens: dollarsPerThousand,
                KnownRows: Known,
                SkippedRows: Skipped);
        }
    }
}
