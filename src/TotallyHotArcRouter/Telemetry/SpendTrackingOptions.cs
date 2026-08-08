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
    /// Gets the compiled-in default of <see cref="LogPath"/>, used to detect whether an operator has
    /// actually configured a custom value (see <see cref="LogPath"/>'s deprecation note).
    /// </summary>
    public const string DefaultLogPath = "spend_log.jsonl";

    /// <summary>
    /// <b>Deprecated (§5.13):</b> the append-only JSON Lines spend log this once configured is no longer
    /// written - the durable usage ledger (<c>docs/router/token-tracking-implementation-plan.md</c>
    /// Phase 2) strictly supersedes it (queryable history, dedup, cost confidence, none of which the flat
    /// file ever had). This property is retained only so <see cref="SpendTracker"/> can detect a
    /// still-configured non-default value and warn the operator once at startup; it no longer influences
    /// any file path.
    /// </summary>
    public string LogPath { get; init; } = DefaultLogPath;
}

