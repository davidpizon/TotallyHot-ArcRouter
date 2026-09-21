namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>
/// Builds the three Report Card chart models (spend by model, grade mix, score delta vs frozen policy)
/// from already-aggregated rows. Static functions on one class, matching the Cost Analytics /
/// Model Distribution split: this library owns the pure chart math, the Razor host only serializes
/// and renders.
/// </summary>
public static class LearningReportCardChartBuilder
{
    /// <summary>Stable A–F fill colors: A/B green (healthy), C amber, D/F rose/red.</summary>
    public static readonly IReadOnlyDictionary<string, string> GradeColors = new Dictionary<string, string>(
        StringComparer.Ordinal)
    {
        ["A"] = "#10b981",
        ["B"] = "#1ed760",
        ["C"] = "#f59e0b",
        ["D"] = "#fb7185",
        ["F"] = "#ef4444"
    };

    /// <summary>Grouped bars of USD spend per model, cost-descending as the aggregator already sorted them.</summary>
    /// <param name="models">Model names, one per bar.</param>
    /// <param name="costs">Matching USD costs.</param>
    public static GroupedBarsModel SpendByModel(IReadOnlyList<string> models, IReadOnlyList<decimal> costs)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(costs);
        var yMax = GroupedBarsModel.DynamicYMax([costs]);
        return new GroupedBarsModel(
            title: "Spend by Model",
            categories: models,
            yMax: yMax,
            series: [new DistributionSeries(Name: "Spend", Color: "#1ed760", Data: costs)],
            unit: "$");
    }

    /// <summary>Donut of letter-grade mix. Slice names reuse <see cref="DistributionSlice.Model"/> as the grade letter.</summary>
    /// <param name="grades">Grade letters in display order.</param>
    /// <param name="percents">Matching mix percentages (0–100).</param>
    public static DonutModel GradeMix(IReadOnlyList<string> grades, IReadOnlyList<decimal> percents)
    {
        ArgumentNullException.ThrowIfNull(grades);
        ArgumentNullException.ThrowIfNull(percents);
        var slices = grades.Select((grade, i) => new DistributionSlice(
            Model: grade,
            Value: i < percents.Count ? percents[i] : 0m,
            Color: GradeColors.TryGetValue(grade, out var color) ? color : ChartPalette.ColorFor(grade))).ToList();
        return new DonutModel(title: "Grade Mix", slices: slices);
    }

    /// <summary>
    /// Signed bars of mean observed-minus-baseline score per model. The axis is symmetric around zero
    /// so a quality win and a quality loss read on the same scale.
    /// </summary>
    /// <param name="models">Model names, one per bar.</param>
    /// <param name="deltas">Matching mean score deltas.</param>
    public static GroupedBarsModel ScoreDelta(IReadOnlyList<string> models, IReadOnlyList<decimal> deltas)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(deltas);
        var extent = GroupedBarsModel.SymmetricExtent(deltas);
        return new GroupedBarsModel(
            title: "Score Delta vs Frozen Policy",
            categories: models,
            yMax: extent,
            series: [new DistributionSeries(Name: "Score delta", Color: "#38bdf8", Data: deltas)],
            unit: "score",
            yMin: extent is { } value ? -value : null);
    }
}
