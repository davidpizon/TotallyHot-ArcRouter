using System.Net;

namespace TotallyHot.ArcRouter.Proxy.Auth;

/// <summary>
/// Pure request-shape checks backing ADR-0012's per-request guard: the Host allowlist (DNS-rebinding
/// defense), the Origin-must-match-or-be-absent check (CSRF defense), and loopback remote-IP detection
/// with IPv4-mapped IPv6 normalized. Deliberately free of <c>HttpContext</c> so every branch is a plain
/// unit test - <see cref="WebPortRequestGuardMiddleware"/> and
/// <see cref="ManagementAuthEndpoints"/> are the only callers that touch the live request.
/// </summary>
public static class LoopbackRequestGuard
{
    /// <summary>
    /// Returns whether <paramref name="host"/> (the request's <c>Host</c> header, host part only - no
    /// port) is in <paramref name="allowedHosts"/>. Comparison is ordinal, case-insensitive, and strips a
    /// wrapping <c>[]</c> pair from either side before comparing: ASP.NET Core's
    /// <see cref="Microsoft.AspNetCore.Http.HostString.Host"/> returns an IPv6 literal *without* brackets
    /// (e.g. <c>::1</c>, not <c>[::1]</c>), while <see cref="WebInterfaceOptions.AllowedHosts"/>' own
    /// default and documentation spell IPv6 entries *with* brackets - a literal, bracket-sensitive
    /// comparison rejected every IPv6 loopback request outright (a real bug this normalization fixes).
    /// </summary>
    public static bool IsHostAllowed(string? host, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var normalizedHost = StripBrackets(host);

        foreach (var allowed in allowedHosts)
            if (string.Equals(a: normalizedHost, b: StripBrackets(allowed), comparisonType: StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Removes one wrapping <c>[</c>/<c>]</c> pair from an IPv6 literal, if present; returns other values unchanged.</summary>
    private static string StripBrackets(string value)
    {
        return value.Length >= 2 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;
    }

    /// <summary>
    /// Returns whether a web-port request is acceptable for ADR-0012's CSRF defense: either
    /// <paramref name="origin"/> is present and exactly equals <paramref name="expectedOrigin"/> (scheme,
    /// host, and port), or <paramref name="origin"/> is absent and <paramref name="secFetchSite"/> is not
    /// <c>"cross-site"</c>.
    /// </summary>
    /// <remarks>
    /// A real, ordinary top-level browser navigation (typing the URL, following a bookmark) never sends
    /// an <c>Origin</c> header, but a modern browser still attaches <c>Sec-Fetch-Site: none</c> to it -
    /// this method originally treated *any* presence of <c>Sec-Fetch-Site</c> as disqualifying once
    /// <c>Origin</c> was absent, which rejected every such navigation with 403 (verified only against
    /// curl, which sends neither header, so this never surfaced until a real browser was tried). The
    /// header's own *value* is what actually distinguishes a same-page/same-site/no-referrer request
    /// (<c>"same-origin"</c>, <c>"same-site"</c>, <c>"none"</c>) from the one shape this check exists to
    /// stop: a plain, Origin-less GET issued from another site's page (<c>"cross-site"</c>) - e.g. an
    /// <c>&lt;img&gt;</c>/<c>&lt;a&gt;</c> pointed at this host from an attacker-controlled page. A caller
    /// sending neither header at all (native/CLI, most non-browser HTTP clients) is treated the same as
    /// a same-site navigation, since it isn't a browser CSRF vector in the first place.
    /// </remarks>
    public static bool IsOriginAllowed(string? origin, string? secFetchSite, string expectedOrigin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return !string.Equals(a: secFetchSite, b: "cross-site", comparisonType: StringComparison.OrdinalIgnoreCase);

        return string.Equals(a: origin, b: expectedOrigin, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether <paramref name="remoteAddress"/> is a loopback address, normalizing an
    /// IPv4-mapped IPv6 address (e.g. <c>::ffff:127.0.0.1</c>, what a dual-stack socket reports for an
    /// IPv4 loopback connection) before checking.
    /// </summary>
    public static bool IsLoopback(IPAddress? remoteAddress)
    {
        if (remoteAddress is null) return false;

        var normalized = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        return IPAddress.IsLoopback(normalized);
    }
}
