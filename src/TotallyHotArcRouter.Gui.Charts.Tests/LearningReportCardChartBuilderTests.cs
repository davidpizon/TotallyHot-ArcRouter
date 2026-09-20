using TotallyHot.ArcRouter.Gui.Charts;

namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Covers <see cref="LearningReportCardChartBuilder"/>: the three Report Card chart models serialize
/// with the renderer field names <c>echarts-interop.js</c> reads, including the new <c>unit</c>/<c>yMin</c>
/// grouped-bar fields.
/// </summary>
public sealed class LearningReportCardChartBuilderTests
{
    [Fact]
    public void SpendByModel_UsesDollarUnit()
    {
        var model = LearningReportCardChartBuilder.SpendByModel(["gpt-4o-mini"], [12.40m]);

        using var doc = System.Text.Json.JsonDocument.Parse(ChartJson.Serialize(model));
        var root = doc.RootElement;
        Assert.Equal(expected: "GroupedBars", actual: root.GetProperty("kind").GetString());
        Assert.Equal(expected: "$", actual: root.GetProperty("unit").GetString());
        Assert.Equal(expected: "gpt-4o-mini", actual: root.GetProperty("categories")[0].GetString());
        Assert.Equal(12.40m, actual: root.GetProperty("series")[0].GetProperty("data")[0].GetDecimal());
    }

    [Fact]
    public void GradeMix_UsesStableGradeColors()
    {
        var model = LearningReportCardChartBuilder.GradeMix(["A", "F"], [80m, 20m]);

        using var doc = System.Text.Json.JsonDocument.Parse(ChartJson.Serialize(model));
        var slices = doc.RootElement.GetProperty("slices");
        Assert.Equal(expected: "A", actual: slices[0].GetProperty("model").GetString());
        Assert.Equal(expected: "#10b981", actual: slices[0].GetProperty("color").GetString());
        Assert.Equal(expected: "F", actual: slices[1].GetProperty("model").GetString());
        Assert.Equal(expected: "#ef4444", actual: slices[1].GetProperty("color").GetString());
    }

    [Fact]
    public void ScoreDelta_UsesSymmetricAxisAndScoreUnit()
    {
        var model = LearningReportCardChartBuilder.ScoreDelta(["kimi-k2.5", "glm-5"], [0.15m, -0.10m]);

        using var doc = System.Text.Json.JsonDocument.Parse(ChartJson.Serialize(model));
        var root = doc.RootElement;
        Assert.Equal(expected: "score", actual: root.GetProperty("unit").GetString());
        Assert.True(root.GetProperty("yMax").GetDecimal() > 0);
        Assert.Equal(
            expected: -root.GetProperty("yMax").GetDecimal(),
            actual: root.GetProperty("yMin").GetDecimal());
    }

    [Fact]
    public void SymmetricExtent_AllZeros_IsNull()
    {
        Assert.Null(GroupedBarsModel.SymmetricExtent([0m, 0m]));
    }
}
