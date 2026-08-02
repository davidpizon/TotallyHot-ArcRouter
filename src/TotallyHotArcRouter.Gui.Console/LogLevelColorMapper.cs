namespace TotallyHot.ArcRouter.Gui.Console;

/// <summary>
/// Maps a log line's level string to the hex color the Console tab renders it in, per the
/// color-coding table in docs/gui/console-tab-plan.md. Matching is case-insensitive since the level
/// arrives over the wire from a server that always sends the normalized uppercase form (see
/// <c>TotallyHot.ArcRouter.Telemetry.TelemetryLogEventSink.NormalizeLevel</c>), but nothing here depends on that.
/// </summary>
public static class LogLevelColorMapper
{
    /// <summary>Gray: low-level diagnostic data.</summary>
    public const string DebugColor = "#A0A0A0";

    /// <summary>Green: standard system operational events.</summary>
    public const string InfoColor = "#4CAF50";

    /// <summary>Yellow: non-blocking anomalies or performance alerts.</summary>
    public const string WarnColor = "#FFC107";

    /// <summary>Red: operational failures requiring intervention.</summary>
    public const string ErrorColor = "#F44336";

    /// <summary>Magenta: total application crash.</summary>
    public const string FatalColor = "#E91E63";

    /// <summary>Fallback for any level not covered by the spec's five defined levels.</summary>
    public const string UnknownColor = DebugColor;

    /// <summary>
    /// Returns the hex color for <paramref name="level"/>. Unrecognized levels (including
    /// <see langword="null"/> or empty) fall back to <see cref="UnknownColor"/> rather than throwing,
    /// since a malformed or future level string must never break the log viewport.
    /// </summary>
    public static string GetColor(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "DEBUG" or "VERBOSE" or "TRACE" => DebugColor,
        "INFO" or "INFORMATION" => InfoColor,
        "WARN" or "WARNING" => WarnColor,
        "ERROR" => ErrorColor,
        "FATAL" or "CRITICAL" => FatalColor,
        _ => UnknownColor,
    };
}

