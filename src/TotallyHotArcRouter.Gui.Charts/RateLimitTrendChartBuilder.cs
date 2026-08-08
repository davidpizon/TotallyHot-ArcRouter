using System.Globalization;

namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>One rate-limit dimension's remaining budget at a point in time, for the trend chart's x-axis.</summary>
/// <param name="T">The bucket's instant as Unix milliseconds (the x-axis position).</param>
/// <param name="Label">Tooltip header, e.g. <c>14:02 UTC</c>.</param>
/// <param name="Remaining">What was left at this instant, or <see langword="null"/> if unknown that bucket.</param>
/// <param name="Limit">The dimension's configured cap at this instant, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitTrendPoint(long T, string Label, long? Remaining, long? Limit);

/// <summary>
/// A rate-limit dimension's remaining-over-time trend chart, serialized to JSON and handed to the ECharts
/// renderer (<c>wwwroot/js/echarts-interop.js</c>'s <c>RateLimitTrendLine</c> kind).
/// </summary>
/// <param name="Kind">Renderer chart-kind discriminator; always <see cref="RateLimitTrendChartKind.RemainingLine"/>.</param>
/// <param name="Title">Chart title, the human-readable dimension label (e.g. <c>Input tokens</c>).</param>
/// <param name="Headline">The most recent remaining value, pre-formatted, or <c>"—"</c> when there is no history.</param>
/// <param name="Points">The bucketed points, chronologically ordered.</param>
public sealed record RateLimitTrendModel(string Kind, string Title, string Headline, IReadOnlyList<RateLimitTrendPoint> Points);

/// <summary>The bespoke chart format the ECharts renderer draws for a rate-limit trend. Serialized as its name.</summary>
public static class RateLimitTrendChartKind
{
    /// <summary>Remaining-over-time: a stepped line with a soft gradient track.</summary>
    public const string RemainingLine = nameof(RemainingLine);
}

/// <summary>
/// Builds the Providers card's rate-limit trend chart model from a dimension's history points (§5.9). Pure
/// and stateless - no dependency on the Gui project's Blazor/MAUI types - so it is unit-testable on any
/// platform, matching <see cref="CostChartBuilder"/>'s shape.
/// </summary>
public static class RateLimitTrendChartBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Builds the trend chart for one dimension from its history points, chronologically ordered.
    /// </summary>
    /// <param name="dimensionName">The dimension's raw header-segment name (e.g. <c>input-tokens</c>), used only to derive <see cref="RateLimitTrendModel.Title"/>.</param>
    /// <param name="points">The dimension's history points (bucket instant, remaining, limit), in any order - sorted here.</param>
    public static RateLimitTrendModel Build(string dimensionName, IReadOnlyList<(DateTimeOffset BucketUtc, long? Remaining, long? Limit)> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionName);
        ArgumentNullException.ThrowIfNull(points);

        var ordered = points.OrderBy(p => p.BucketUtc).ToList();
        var chartPoints = ordered
            .Select(p => new RateLimitTrendPoint(
                p.BucketUtc.ToUnixTimeMilliseconds(),
                p.BucketUtc.ToUniversalTime().ToString("HH:mm 'UTC'", Inv),
                p.Remaining,
                p.Limit))
            .ToList();

        var headline = ordered.Count > 0 && ordered[^1].Remaining is { } last
            ? last.ToString("N0", Inv)
            : "—";

        return new RateLimitTrendModel(RateLimitTrendChartKind.RemainingLine, DimensionLabel(dimensionName), headline, chartPoints);
    }

    // "input-tokens" -> "Input tokens" - readable label, mirroring ProvidersAdmin.razor's DimensionLabel.
    private static string DimensionLabel(string dimensionName) =>
        dimensionName.Length == 0 ? dimensionName : dimensionName[..1].ToUpperInvariant() + dimensionName[1..].Replace('-', ' ');
}
