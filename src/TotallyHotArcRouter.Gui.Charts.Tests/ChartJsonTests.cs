using System.Text.Json;

namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Guards the JSON contract between the C# chart models and the ECharts renderer
/// (<c>wwwroot/js/echarts-interop.js</c>): the renderer reads camelCase field names (<c>kind</c>,
/// <c>t</c>, <c>tip</c>, <c>yMax</c>, <c>segments</c>, ...), so a rename on either side must break here.
/// </summary>
public class ChartJsonTests
{
    [Fact]
    public void Serialize_CostChartModel_UsesRendererFieldNames()
    {
        var model = new CostChartModel(
            Kind: CostChartKind.SegmentedStepBars,
            Title: "Tool Execution Steps",
            Unit: "steps",
            Headline: "8",
            null,
            Models: [new CostModelColor(Model: "a", Color: "#111111")],
            Points:
            [
                new CostChartPoint(
                    1_720_000_000_000,
                    Label: "Turn #1",
                    Model: "a",
                    Color: "#111111",
                    8m,
                    0m,
                    false,
                    Tip: ["a: 8 steps"],
                    Segments: [new CostChartSegment(Model: "a", Color: "#111111", 8)])
            ]);

        using var doc = JsonDocument.Parse(ChartJson.Serialize(model));
        var root = doc.RootElement;

        Assert.Equal(expected: "SegmentedStepBars", actual: root.GetProperty("kind").GetString());
        Assert.Equal(expected: "steps", actual: root.GetProperty("unit").GetString());
        var point = root.GetProperty("points")[0];
        Assert.Equal(1_720_000_000_000, actual: point.GetProperty("t").GetInt64());
        Assert.Equal(expected: "Turn #1", actual: point.GetProperty("label").GetString());
        Assert.False(point.GetProperty("flag").GetBoolean());
        Assert.Equal(expected: "a: 8 steps", actual: point.GetProperty("tip")[0].GetString());
        Assert.Equal(8, actual: point.GetProperty("segments")[0].GetProperty("steps").GetInt32());
    }

    [Fact]
    public void Serialize_NullThreshold_IsOmitted()
    {
        var model = new CostChartModel(Kind: CostChartKind.CacheGradientLine, Title: "Cache", Unit: "%", Headline: "0%",
            null, Models: [], Points: []);

        using var doc = JsonDocument.Parse(ChartJson.Serialize(model));

        Assert.False(doc.RootElement.TryGetProperty(propertyName: "threshold", value: out _));
    }

    [Fact]
    public void Serialize_GroupedBarsModel_UsesRendererFieldNames()
    {
        var model = new GroupedBarsModel(
            title: "Token Volume Histogram",
            categories: ["Mon", "Tue"],
            6_000_000m,
            series: [new DistributionSeries(Name: "Prompt", Color: "#38bdf8", Data: [2_840_000m, 3_120_000m])]);

        using var doc = JsonDocument.Parse(ChartJson.Serialize(model));
        var root = doc.RootElement;

        Assert.Equal(expected: "GroupedBars", actual: root.GetProperty("kind").GetString());
        Assert.Equal(6_000_000, actual: root.GetProperty("yMax").GetDecimal());
        Assert.Equal(expected: "Prompt", actual: root.GetProperty("series")[0].GetProperty("name").GetString());
    }
}