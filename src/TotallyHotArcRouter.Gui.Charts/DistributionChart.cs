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
public sealed record GroupedBarsModel(
    string Kind,
    string Title,
    IReadOnlyList<string> Categories,
    decimal? YMax,
    IReadOnlyList<DistributionSeries> Series)
{
    /// <summary>Creates a grouped-bars model with the <c>GroupedBars</c> renderer kind.</summary>
    public GroupedBarsModel(string title, IReadOnlyList<string> categories, decimal? yMax,
        IReadOnlyList<DistributionSeries> series)
        : this(Kind: "GroupedBars", Title: title, Categories: categories, YMax: yMax, Series: series)
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