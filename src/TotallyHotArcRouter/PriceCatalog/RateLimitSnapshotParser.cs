using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One captured <c>anthropic-ratelimit-*</c> response header, as persisted verbatim in
/// <c>provider_rate_limit_snapshot</c>.
/// </summary>
/// <param name="HeaderName">The lowercase header name (e.g. <c>anthropic-ratelimit-input-tokens-remaining</c>).</param>
/// <param name="HeaderValue">The header's raw, unparsed value.</param>
public sealed record RateLimitHeaderRow(string HeaderName, string HeaderValue);

/// <summary>
/// A standard-family rate-limit dimension (<c>requests</c>, <c>input-tokens</c>, <c>output-tokens</c>, or
/// <c>tokens</c>): the <c>-limit</c>/<c>-remaining</c>/<c>-reset</c> header trio for one dimension. Any field
/// is <see langword="null"/> when its header wasn't present or didn't parse - Anthropic rounds
/// <c>remaining</c> to the nearest thousand, so these are display values, not exact counts.
/// </summary>
/// <param name="Limit">The dimension's configured cap, or <see langword="null"/> if the <c>-limit</c> header was absent/unparsable.</param>
/// <param name="Remaining">What's left before the cap, or <see langword="null"/> if the <c>-remaining</c> header was absent/unparsable.</param>
/// <param name="ResetAt">When the dimension resets, or <see langword="null"/> if the <c>-reset</c> header was absent/unparsable.</param>
public sealed record RateLimitDimensionView(long? Limit, long? Remaining, DateTimeOffset? ResetAt);

/// <summary>
/// One unified-family time window (e.g. <c>5h</c>, the rolling five-hour window; or a weekly variant): the
/// <c>-status</c>/<c>-remaining</c>/<c>-reset</c> header trio for that window.
/// </summary>
/// <param name="Status">The window's raw status string (e.g. <c>"allowed"</c>), or <see langword="null"/> if absent.</param>
/// <param name="Remaining">What's left in the window, or <see langword="null"/> if absent/unparsable.</param>
/// <param name="ResetAt">When the window resets, or <see langword="null"/> if absent/unparsable.</param>
public sealed record RateLimitWindowView(string? Status, long? Remaining, DateTimeOffset? ResetAt);

/// <summary>
/// The typed projection of one provider's captured <c>anthropic-ratelimit-*</c> headers, produced by
/// <see cref="RateLimitSnapshotParser.Parse"/>. Both header families can be populated at once (a provider
/// proxying both standard-key and subscription-OAuth traffic sees both), and <see cref="RawHeaders"/> always
/// carries every captured header verbatim so nothing this parser doesn't specifically recognize is dropped.
/// </summary>
/// <param name="StandardDimensions">
/// Standard-family dimensions keyed by name (<c>requests</c>, <c>input-tokens</c>, <c>output-tokens</c>,
/// <c>tokens</c>). A dimension is present only when at least one of its three headers was captured.
/// </param>
/// <param name="UnifiedStatus">The unified family's top-level <c>anthropic-ratelimit-unified-status</c>, or <see langword="null"/> if absent.</param>
/// <param name="UnifiedResetAt">The unified family's top-level <c>anthropic-ratelimit-unified-reset</c>, or <see langword="null"/> if absent/unparsable.</param>
/// <param name="UnifiedWindows">
/// Unified-family time windows keyed by their header-name segment (e.g. <c>5h</c>), discovered generically
/// from any <c>anthropic-ratelimit-unified-{window}-{status|remaining|reset}</c> header - so a future window
/// Anthropic adds is captured and parsed without a code change.
/// </param>
/// <param name="RepresentativeClaim">The unified family's <c>anthropic-ratelimit-unified-representative-claim</c>, or <see langword="null"/> if absent.</param>
/// <param name="RawHeaders">Every captured header, verbatim, keyed by lowercase name - the fallback for anything this parser doesn't specifically recognize.</param>
public sealed record RateLimitSnapshotView(
    IReadOnlyDictionary<string, RateLimitDimensionView> StandardDimensions,
    string? UnifiedStatus,
    DateTimeOffset? UnifiedResetAt,
    IReadOnlyDictionary<string, RateLimitWindowView> UnifiedWindows,
    string? RepresentativeClaim,
    IReadOnlyDictionary<string, string> RawHeaders);

/// <summary>
/// Projects raw <c>anthropic-ratelimit-*</c> header rows into a typed <see cref="RateLimitSnapshotView"/>.
/// Pure and stateless - no I/O - so it is unit-testable against fixture rows without a database.
/// </summary>
public static class RateLimitSnapshotParser
{
    private const string Prefix = "anthropic-ratelimit-";
    private const string UnifiedPrefix = "anthropic-ratelimit-unified-";

    private static readonly string[] StandardDimensionNames = ["requests", "input-tokens", "output-tokens", "tokens"];

    /// <summary>
    /// Parses a provider's captured rate-limit header rows into a typed view. Rows with an unrecognized
    /// header name still appear in <see cref="RateLimitSnapshotView.RawHeaders"/> - parsing only adds
    /// structure, it never drops data.
    /// </summary>
    public static RateLimitSnapshotView Parse(IReadOnlyList<RateLimitHeaderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            raw[row.HeaderName] = row.HeaderValue;
        }

        var standard = new Dictionary<string, RateLimitDimensionView>(StringComparer.OrdinalIgnoreCase);
        foreach (var dimension in StandardDimensionNames)
        {
            var limitHeader = $"{Prefix}{dimension}-limit";
            var remainingHeader = $"{Prefix}{dimension}-remaining";
            var resetHeader = $"{Prefix}{dimension}-reset";

            // A dimension is present the moment any of its three headers was captured - even if that
            // header's value failed to parse (see the malformed-value case below), the header existing at
            // all means the API reported this dimension.
            if (!raw.ContainsKey(limitHeader) && !raw.ContainsKey(remainingHeader) && !raw.ContainsKey(resetHeader))
            {
                continue;
            }

            standard[dimension] = new RateLimitDimensionView(
                ReadLong(raw, limitHeader),
                ReadLong(raw, remainingHeader),
                ReadDate(raw, resetHeader));
        }

        var unifiedStatus = ReadString(raw, $"{UnifiedPrefix}status");
        var unifiedResetAt = ReadDate(raw, $"{UnifiedPrefix}reset");
        var representativeClaim = ReadString(raw, $"{UnifiedPrefix}representative-claim");

        var windows = new Dictionary<string, RateLimitWindowView>(StringComparer.OrdinalIgnoreCase);
        foreach (var (headerName, headerValue) in raw)
        {
            if (!headerName.StartsWith(UnifiedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = headerName[UnifiedPrefix.Length..];
            if (string.Equals(suffix, "status", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(suffix, "reset", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(suffix, "representative-claim", StringComparison.OrdinalIgnoreCase))
            {
                // Already handled as top-level unified fields above, not a per-window one.
                continue;
            }

            var dashIndex = suffix.LastIndexOf('-');
            if (dashIndex <= 0 || dashIndex == suffix.Length - 1)
            {
                continue;
            }

            var windowName = suffix[..dashIndex];
            var field = suffix[(dashIndex + 1)..];

            windows.TryGetValue(windowName, out var window);
            window ??= new RateLimitWindowView(null, null, null);

            window = field.ToLowerInvariant() switch
            {
                "status" => window with { Status = headerValue },
                "remaining" => window with { Remaining = ParseLong(headerValue) },
                "reset" => window with { ResetAt = ParseDate(headerValue) },
                _ => window,
            };

            windows[windowName] = window;
        }

        return new RateLimitSnapshotView(standard, unifiedStatus, unifiedResetAt, windows, representativeClaim, raw);
    }

    private static string? ReadString(IReadOnlyDictionary<string, string> raw, string headerName) =>
        raw.TryGetValue(headerName, out var value) ? value : null;

    private static long? ReadLong(IReadOnlyDictionary<string, string> raw, string headerName) =>
        raw.TryGetValue(headerName, out var value) ? ParseLong(value) : null;

    private static DateTimeOffset? ReadDate(IReadOnlyDictionary<string, string> raw, string headerName) =>
        raw.TryGetValue(headerName, out var value) ? ParseDate(value) : null;

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    // RFC 3339, matching the format Anthropic publishes for every *-reset header.
    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
