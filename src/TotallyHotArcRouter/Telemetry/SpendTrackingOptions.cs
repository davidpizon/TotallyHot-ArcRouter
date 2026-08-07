namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Configuration for the personal-scale running spend total, bound from the <c>SpendTracking</c>
/// section.
/// </summary>
public sealed class SpendTrackingOptions
{
    /// <summary>
    /// Gets the configuration section name used for spend-tracking settings.
    /// </summary>
    public const string SectionName = "SpendTracking";

    /// <summary>
    /// Gets a value indicating whether spend is tracked and logged at all. Default <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets the path to the append-only JSON Lines spend log. Relative paths are resolved from the
    /// application base directory, mirroring <see cref="TotallyHot.ArcRouter.Models.RoutingOptions.MemoryPath"/>.
    /// </summary>
    public string LogPath { get; init; } = "spend_log.jsonl";
}

