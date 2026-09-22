using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>One named, colored series of the Model Distribution token-volume grouped bar chart.</summary>
public sealed record DistributionSeries(string Name, string Color, IReadOnlyList<decimal> Data);

/// <summary>One slice of the Model Distribution market-share donut.</summary>
public sealed record DistributionSlice(string Model, decimal Value, string Color);

/// <summary>The Model Distribution token-volume grouped bar chart, for the ECharts renderer.</summary>
/// <param name="Kind">Renderer discriminator, always <c>GroupedBars</c>.</param>
/// <param name="Title">Chart title.</param>
/// <param name="Categories">X-axis category labels (e.g. weekday slots).</param>
/// <param name="YMax">Optional fixed y-axis maximum.</param>
/// <param name="Series">The bar series (prompt / completion).</param>
/// <param name="Unit">
/// Axis/tooltip unit forwarded to the renderer (<c>tok</c>, <c>$</c>, <c>score</c>). Null keeps the
/// historical token formatter so existing Model Distribution / budget charts stay unchanged.
/// </param>
/// <param name="YMin">Optional fixed y-axis minimum, used by the score-delta chart's symmetric axis.</param>
public sealed record GroupedBarsModel(
    string Kind,
    string Title,
    IReadOnlyList<string> Categories,
    decimal? YMax,
    IReadOnlyList<DistributionSeries> Series,
    string? Unit = null,
    decimal? YMin = null)
{
    /// <summary>Creates a grouped-bars model with the <c>GroupedBars</c> renderer kind.</summary>
    public GroupedBarsModel(string title, IReadOnlyList<string> categories, decimal? yMax,
        IReadOnlyList<DistributionSeries> series, string? unit = null, decimal? yMin = null)
        : this(Kind: "GroupedBars", Title: title, Categories: categories, YMax: yMax, Series: series, Unit: unit,
            YMin: yMin)
    {
    }

    /// <summary>
    /// Computes a y-axis maximum with headroom above the largest value across every series, or
    /// <see langword="null"/> when there is no positive data - the ECharts renderer auto-scales the axis
    /// in that case (see <c>echarts-interop.js</c>'s <c>m.yMax || null</c>). Replaces a chart-specific
    /// hardcoded maximum with one derived from whatever data is actually being rendered, so the axis stays
    /// meaningful as real traffic volume grows or shrinks instead of staying pinned to a value chosen for
    /// mock data.
    /// </summary>
    /// <param name="series">Every series' data that will share this axis.</param>
    /// <param name="headroomMultiplier">How much larger than the largest value the axis maximum should be.</param>
    public static decimal? DynamicYMax(IEnumerable<IReadOnlyList<decimal>> series, decimal headroomMultiplier = 1.1m)
    {
        var max = series.SelectMany(s => s).DefaultIfEmpty(0m).Max();
        return max <= 0m ? null : Math.Ceiling(max * headroomMultiplier);
    }

    /// <summary>
    /// Symmetric axis extent around zero for a signed series (score delta): the larger of the absolute
    /// min/max, with headroom. Null when every value is zero so the renderer auto-scales rather than
    /// pinning a fabricated range.
    /// </summary>
    /// <param name="values">The signed values that will share the axis.</param>
    /// <param name="headroomMultiplier">How much larger than the largest absolute value the extent should be.</param>
    public static decimal? SymmetricExtent(IEnumerable<decimal> values, decimal headroomMultiplier = 1.1m)
    {
        var maxAbs = values.Select(Math.Abs).DefaultIfEmpty(0m).Max();
        return maxAbs <= 0m ? null : Math.Round(maxAbs * headroomMultiplier, decimals: 3);
    }
}

/// <summary>The Model Distribution market-share donut chart, for the ECharts renderer.</summary>
/// <param name="Kind">Renderer discriminator, always <c>Donut</c>.</param>
/// <param name="Title">Chart title.</param>
/// <param name="Slices">The donut slices.</param>
public sealed record DonutModel(string Kind, string Title, IReadOnlyList<DistributionSlice> Slices)
{
    /// <summary>Creates a donut model with the <c>Donut</c> renderer kind.</summary>
    public DonutModel(string title, IReadOnlyList<DistributionSlice> slices)
        : this(Kind: "Donut", Title: title, Slices: slices)
    {
    }
}

/// <summary>
/// Serializes chart models to the camelCase JSON the ECharts renderer (<c>echarts-interop.js</c>)
/// expects. One shared, cached options instance keeps property names (<c>kind</c>, <c>t</c>,
/// <c>yMax</c>, ...) in sync with the JS field names.
/// </summary>
public static class ChartJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes a chart model to a JSON string for <c>echartsInterop.render</c>.</summary>
    public static string Serialize<T>(T model)
    {
        return JsonSerializer.Serialize(value: model, options: Options);
    }
}