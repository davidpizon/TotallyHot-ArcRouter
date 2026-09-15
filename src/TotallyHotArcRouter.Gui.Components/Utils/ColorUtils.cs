using TotallyHot.ArcRouter.Gui.Charts;

namespace TotallyHot.ArcRouter.Gui.Utils;

/// <summary>Deterministic display-color assignment for agents shown across the dashboard.</summary>
public static class ColorUtils
{
    /// <summary>
    /// Maps an agent/model name to a stable palette color. Delegates to the pure
    /// <see cref="ChartPalette"/> in the Charts library so the Blazor components and the chart builders
    /// share one palette (and one FNV-1a hash) rather than drifting apart.
    /// </summary>
    public static string GetColorForAgent(string? agentName)
    {
        return ChartPalette.ColorFor(agentName);
    }
}