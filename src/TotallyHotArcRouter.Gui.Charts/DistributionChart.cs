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
    public GroupedBarsModel(string title, IReadOnlyList<string> categories, decimal? yMax, IReadOnlyList<DistributionSeries> series)
        : this("GroupedBars", title, categories, yMax, series)
    {
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
        : this("Donut", title, slices)
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a chart model to a JSON string for <c>echartsInterop.render</c>.</summary>
    public static string Serialize<T>(T model) => JsonSerializer.Serialize(model, Options);
}

