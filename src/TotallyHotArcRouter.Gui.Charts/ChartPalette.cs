namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>
/// Deterministic display-color assignment for models/agents across every chart. Lives in the pure
/// Charts library so the chart builders and the Gui's <c>ColorUtils</c> (which delegates here) agree on
/// one palette. Uses FNV-1a rather than <see cref="string.GetHashCode()"/>, which is randomized per
/// process and would reshuffle colors on every launch.
/// </summary>
public static class ChartPalette
{
    private static readonly string[] Palette =
    [
        "#10b981", // emerald
        "#38bdf8", // cyan
        "#818cf8", // indigo
        "#fb7185", // rose
        "#f59e0b", // amber
        "#a78bfa", // purple
        "#14b8a6", // teal
        "#0ea5e9", // sky
        "#6366f1", // indigo-2
        "#ec4899", // pink
        "#f97316", // orange
        "#06b6d4" // cyan-2
    ];

    /// <summary>Maps a model/agent name to a stable palette color.</summary>
    public static string ColorFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return Palette[0];

        var hash = 2166136261u;
        foreach (var c in key) hash = (hash ^ c) * 16777619u;

        return Palette[(int)(hash % (uint)Palette.Length)];
    }
}