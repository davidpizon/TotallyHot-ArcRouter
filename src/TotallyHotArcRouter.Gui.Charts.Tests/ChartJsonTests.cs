using System.Text.Json;
using TotallyHot.ArcRouter.Gui.Charts;

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
            CostChartKind.SegmentedStepBars,
            "Tool Execution Steps",
            "steps",
            "8",
            null,
            [new CostModelColor("a", "#111111")],
            [
                new CostChartPoint(
                    T: 1_720_000_000_000,
                    Label: "Turn #1",
                    Model: "a",
                    Color: "#111111",
                    Value: 8m,
                    Secondary: 0m,
                    Flag: false,
                    Tip: ["a: 8 steps"],
                    Segments: [new CostChartSegment("a", "#111111", 8)]),
            ]);

        using var doc = JsonDocument.Parse(ChartJson.Serialize(model));
        var root = doc.RootElement;

        Assert.Equal("SegmentedStepBars", root.GetProperty("kind").GetString());
        Assert.Equal("steps", root.GetProperty("unit").GetString());
        var point = root.GetProperty("points")[0];
        Assert.Equal(1_720_000_000_000, point.GetProperty("t").GetInt64());
        Assert.Equal("Turn #1", point.GetProperty("label").GetString());
        Assert.False(point.GetProperty("flag").GetBoolean());
        Assert.Equal("a: 8 steps", point.GetProperty("tip")[0].GetString());
        Assert.Equal(8, point.GetProperty("segments")[0].GetProperty("steps").GetInt32());
    }

    [Fact]
    public void Serialize_NullThreshold_IsOmitted()
    {
        var model = new CostChartModel(CostChartKind.CacheGradientLine, "Cache", "%", "0%", null, [], []);

        using var doc = JsonDocument.Parse(ChartJson.Serialize(model));

        Assert.False(doc.RootElement.TryGetProperty("threshold", out _));
    }

    [Fact]
    public void Serialize_GroupedBarsModel_UsesRendererFieldNames()
    {
        var model = new GroupedBarsModel(
            "Token Volume Histogram",
            ["Mon", "Tue"],
            6_000_000m,
            [new DistributionSeries("Prompt", "#38bdf8", [2_840_000m, 3_120_000m])]);

        using var doc = JsonDocument.Parse(ChartJson.Serialize(model));
        var root = doc.RootElement;

        Assert.Equal("GroupedBars", root.GetProperty("kind").GetString());
        Assert.Equal(6_000_000, root.GetProperty("yMax").GetDecimal());
        Assert.Equal("Prompt", root.GetProperty("series")[0].GetProperty("name").GetString());
    }
}

