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
    /// port) is in <paramref name="allowedHosts"/>. Comparison is ordinal, case-insensitive; entries in
    /// <paramref name="allowedHosts"/> that carry brackets (e.g. <c>[::1]</c>) are compared bracket-for-
    /// bracket since that is how <see cref="WebInterfaceOptions.AllowedHosts"/> is documented and
    /// defaulted.
    /// </summary>
    public static bool IsHostAllowed(string? host, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        foreach (var allowed in allowedHosts)
            if (string.Equals(a: host, b: allowed, comparisonType: StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Returns whether <paramref name="origin"/> is acceptable for a same-origin web-port request:
    /// either absent (a native/CLI caller with no browser <c>Sec-Fetch-Site</c> context - see
    /// <paramref name="secFetchSitePresent"/>) or exactly equal to <paramref name="expectedOrigin"/>
    /// (scheme, host, and port). A present <c>Origin</c> with no matching <c>Sec-Fetch-Site</c> is
    /// treated the same as an absent one for that reason: the two headers are set by the same browser
    /// request pipeline, so a caller offering neither is not a browser at all, and a caller offering
    /// <c>Origin</c> without the fetch metadata is unusual enough (proxies stripping headers, older
    /// clients) that requiring an exact match rather than guessing is the safer default.
    /// </summary>
    public static bool IsOriginAllowed(string? origin, bool secFetchSitePresent, string expectedOrigin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return !secFetchSitePresent;

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
