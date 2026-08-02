namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Bounds text broadcast over the telemetry stream (request/response previews - see
/// <see cref="RoutingTelemetryEvent"/>) so a pathological input (a huge pasted file, a very long
/// generated response) can't produce an outsized gRPC message.
/// </summary>
public static class TextTruncator
{
    /// <summary>Default cap: roughly a long paragraph or a small code snippet.</summary>
    public const int DefaultMaxLength = 2000;

    /// <summary>
    /// Returns <paramref name="text"/> unchanged if it's <see langword="null"/> or at most
    /// <paramref name="maxLength"/> characters; otherwise truncates to <paramref name="maxLength"/>
    /// characters with a trailing "…" marker.
    /// </summary>
    public static string? Truncate(string? text, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLength, 0);

        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, maxLength), "…");
    }
}

