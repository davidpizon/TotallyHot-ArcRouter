using System.Globalization;
using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One captured <c>anthropic-ratelimit-*</c> or <c>x-ratelimit-*</c> response header, as persisted
/// verbatim in <c>provider_rate_limit_snapshot</c>.
/// </summary>
/// <param name="HeaderName">The lowercase header name (e.g. <c>anthropic-ratelimit-input-tokens-remaining</c> or <c>x-ratelimit-remaining-tokens</c>).</param>
/// <param name="HeaderValue">The header's raw, unparsed value.</param>
public sealed record RateLimitHeaderRow(string HeaderName, string HeaderValue);

/// <summary>
/// One provider's captured rate-limit headers for a single minute bucket, as returned by
/// <see cref="RateLimitRepository.GetRateLimitHistory"/>. Carries only the headers actually captured in
/// that bucket (not filled forward from a prior one) - callers reuse <see cref="RateLimitSnapshotParser.Parse"/>
/// per bucket, exactly as <see cref="RateLimitRepository.GetRateLimitSnapshot"/>'s callers do for the
/// latest snapshot.
/// </summary>
/// <param name="BucketUtc">The minute bucket's start instant, UTC.</param>
/// <param name="Headers">The headers captured within this bucket.</param>
public sealed record RateLimitHistoryBucket(DateTimeOffset BucketUtc, IReadOnlyList<RateLimitHeaderRow> Headers);

/// <summary>
/// A standard-family rate-limit dimension: Anthropic's <c>requests</c>, <c>input-tokens</c>,
/// <c>output-tokens</c>, or <c>tokens</c>; or OpenAI's <c>requests</c>/<c>tokens</c>
/// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §6.2) - the <c>-limit</c>/<c>-remaining</c>/
/// <c>-reset</c> header trio for one dimension, regardless of which family reported it. Any field is
/// <see langword="null"/> when its header wasn't present or didn't parse - Anthropic rounds
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
/// The typed projection of one provider's captured <c>anthropic-ratelimit-*</c>/<c>x-ratelimit-*</c>
/// headers, produced by <see cref="RateLimitSnapshotParser.Parse"/>. Multiple families can be populated at
/// once (a provider proxying both standard-key and subscription-OAuth Anthropic traffic sees both of
/// Anthropic's own families; an OpenAI provider's headers land in <see cref="StandardDimensions"/>
/// alongside Anthropic's), and <see cref="RawHeaders"/> always carries every captured header verbatim so
/// nothing this parser doesn't specifically recognize is dropped.
/// </summary>
/// <param name="StandardDimensions">
/// Standard-family dimensions keyed by name: Anthropic's <c>requests</c>, <c>input-tokens</c>,
/// <c>output-tokens</c>, <c>tokens</c>; or OpenAI's <c>requests</c>, <c>tokens</c>. A dimension is present
/// only when at least one of its three headers was captured. Anthropic and OpenAI share dimension names
/// that overlap (<c>requests</c>, <c>tokens</c>); on the rare capture where both families are present for
/// one provider, OpenAI's entry is keyed <c>x-ratelimit-{dimension}</c> instead so it doesn't overwrite
/// Anthropic's - a real provider is normally one family or the other, so this only matters for a mixed
/// capture.
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
/// Projects raw <c>anthropic-ratelimit-*</c>/<c>x-ratelimit-*</c> header rows into a typed
/// <see cref="RateLimitSnapshotView"/>. Pure and stateless - no I/O - so it is unit-testable against
/// fixture rows without a database.
/// </summary>
public static class RateLimitSnapshotParser
{
    private const string Prefix = "anthropic-ratelimit-";
    private const string UnifiedPrefix = "anthropic-ratelimit-unified-";

    // OpenAI's header naming reverses Anthropic's segment order: "x-ratelimit-{field}-{dimension}"
    // (e.g. x-ratelimit-remaining-tokens) rather than Anthropic's "anthropic-ratelimit-{dimension}-{field}"
    // - so it needs its own loop below rather than sharing StandardDimensionNames' string-format.
    private const string OpenAiPrefix = "x-ratelimit-";

    private static readonly string[] StandardDimensionNames = ["requests", "input-tokens", "output-tokens", "tokens"];
    private static readonly string[] OpenAiDimensionNames = ["requests", "tokens"];

    // Matches one Go duration token (magnitude + unit), e.g. "6" + "m" out of "6m0s". "ms" must be
    // ordered before "m" in the alternation so a millisecond suffix isn't mistaken for a minute one.
    private static readonly Regex GoDurationTokenPattern = new(@"(\d+(?:\.\d+)?)(ms|h|m|s)", RegexOptions.Compiled);

    /// <summary>
    /// Parses a provider's captured rate-limit header rows into a typed view. Rows with an unrecognized
    /// header name still appear in <see cref="RateLimitSnapshotView.RawHeaders"/> - parsing only adds
    /// structure, it never drops data.
    /// </summary>
    /// <param name="rows">The provider's captured header rows.</param>
    /// <param name="observedAtUtc">
    /// When the headers were captured, needed to resolve OpenAI's <c>x-ratelimit-reset-*</c> headers - a
    /// Go-style duration <em>relative to the response</em> (e.g. <c>"6m0s"</c>), not an absolute timestamp
    /// like Anthropic's RFC 3339 <c>-reset</c> headers. <see langword="null"/> (the default) leaves any
    /// OpenAI reset field unresolved (<see langword="null"/>) rather than guessing at "now".
    /// </param>
    public static RateLimitSnapshotView Parse(IReadOnlyList<RateLimitHeaderRow> rows, DateTimeOffset? observedAtUtc = null)
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

        foreach (var dimension in OpenAiDimensionNames)
        {
            var limitHeader = $"{OpenAiPrefix}limit-{dimension}";
            var remainingHeader = $"{OpenAiPrefix}remaining-{dimension}";
            var resetHeader = $"{OpenAiPrefix}reset-{dimension}";

            if (!raw.ContainsKey(limitHeader) && !raw.ContainsKey(remainingHeader) && !raw.ContainsKey(resetHeader))
            {
                continue;
            }

            // Disambiguate only on collision: a real provider is Anthropic-shaped or OpenAI-shaped, never
            // both, so this loop's dimension names normally land in fresh dict slots. But the capture layer
            // doesn't itself prevent both families from appearing for one provider key, and silently
            // overwriting the Anthropic loop's entry above would lose that data rather than merely being
            // redundant with it.
            var key = standard.ContainsKey(dimension) ? $"{OpenAiPrefix}{dimension}" : dimension;

            standard[key] = new RateLimitDimensionView(
                ReadLong(raw, limitHeader),
                ReadLong(raw, remainingHeader),
                ReadGoDurationResetDate(raw, resetHeader, observedAtUtc));
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

    /// <summary>Returns the raw string value of <paramref name="headerName"/>, or <see langword="null"/> if absent.</summary>
    private static string? ReadString(IReadOnlyDictionary<string, string> raw, string headerName) =>
        raw.TryGetValue(headerName, out var value) ? value : null;

    /// <summary>Reads and parses <paramref name="headerName"/> as an integer, or <see langword="null"/> if absent or malformed.</summary>
    private static long? ReadLong(IReadOnlyDictionary<string, string> raw, string headerName) =>
        raw.TryGetValue(headerName, out var value) ? ParseLong(value) : null;

    /// <summary>Reads and parses <paramref name="headerName"/> as an RFC 3339 date, or <see langword="null"/> if absent or malformed.</summary>
    private static DateTimeOffset? ReadDate(IReadOnlyDictionary<string, string> raw, string headerName) =>
        raw.TryGetValue(headerName, out var value) ? ParseDate(value) : null;

    /// <summary>Resolves an OpenAI <c>x-ratelimit-reset-*</c> header (a Go-style duration relative to the response) into an absolute instant, or <see langword="null"/> when the header is absent, malformed, or <paramref name="observedAtUtc"/> wasn't supplied.</summary>
    private static DateTimeOffset? ReadGoDurationResetDate(IReadOnlyDictionary<string, string> raw, string headerName, DateTimeOffset? observedAtUtc)
    {
        if (!raw.TryGetValue(headerName, out var value) || observedAtUtc is not { } observed)
        {
            return null;
        }

        return ParseGoDuration(value) is { } duration ? observed + duration : null;
    }

    /// <summary>Parses <paramref name="value"/> as an invariant-culture integer, or <see langword="null"/> if malformed.</summary>
    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    // RFC 3339, matching the format Anthropic publishes for every *-reset header.
    /// <summary>Parses <paramref name="value"/> as an RFC 3339 date, or <see langword="null"/> if malformed.</summary>
    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Parses a Go-style duration string (e.g. <c>"1s"</c>, <c>"6m0s"</c>, <c>"23h"</c>) - the format
    /// OpenAI's <c>x-ratelimit-reset-*</c> headers use - into a <see cref="TimeSpan"/> by summing each
    /// magnitude+unit token. Returns <see langword="null"/> for an empty string, a string with no
    /// recognizable tokens, or one with a trailing unparseable remainder (a partial parse is worse than
    /// surfacing the raw string unparsed, which <see cref="RateLimitSnapshotView.RawHeaders"/> always does
    /// regardless).
    /// </summary>
    private static TimeSpan? ParseGoDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var matches = GoDurationTokenPattern.Matches(value);
        if (matches.Count == 0)
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var consumedLength = 0;

        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                return null;
            }

            total += match.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "ms" => TimeSpan.FromMilliseconds(amount),
                _ => TimeSpan.Zero,
            };

            consumedLength += match.Length;
        }

        return consumedLength == value.Length ? total : null;
    }
}
