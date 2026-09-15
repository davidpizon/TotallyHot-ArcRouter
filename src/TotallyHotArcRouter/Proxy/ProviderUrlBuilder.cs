using System.Net;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// The two URL rules every provider-probing path shares: where a provider's OpenAI-shaped model list
/// lives, and where its host root is.
/// </summary>
/// <remarks>
/// Extracted because three call sites had independently grown byte-identical copies of
/// <see cref="BuildModelsUrl"/> - <c>ManagementFacade.DiscoverModelsCoreAsync</c>,
/// <c>ProviderEndpointScanner</c>, and now <c>ModelDialectResolver</c>. Duplicated rules do not stay
/// duplicated: the moment one copy is corrected for a provider shape the others have not met, model
/// discovery and capability scanning start disagreeing about what a provider's URL even is, and the
/// symptom (a provider that discovers models fine but scans as unreachable) points nowhere near the cause.
/// </remarks>
internal static class ProviderUrlBuilder
{
    /// <summary>
    /// Builds the OpenAI-compatible model-list URL for a provider base URL: a base ending in <c>/v1</c>,
    /// or containing <c>/v1beta</c> anywhere, gets <c>/models</c> appended; anything else gets
    /// <c>/v1/models</c>.
    /// </summary>
    /// <remarks>
    /// The two halves are deliberately asymmetric rather than an oversight worth "tidying": <c>/v1beta</c>
    /// is matched by <see cref="string.Contains(string, StringComparison)"/> because Gemini-style bases
    /// carry it as a path segment that is not always terminal. Restating the rule as "ends in a version
    /// segment" would describe something stricter than this implements and invite an edit that silently
    /// breaks those bases.
    /// </remarks>
    /// <param name="baseUrl">The provider's configured base URL.</param>
    public static string BuildModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var hasVersionSegment = trimmed.EndsWith(value: "/v1", comparisonType: StringComparison.OrdinalIgnoreCase)
                                || trimmed.Contains(value: "/v1beta",
                                    comparisonType: StringComparison.OrdinalIgnoreCase);
        return hasVersionSegment ? $"{trimmed}/models" : $"{trimmed}/v1/models";
    }

    /// <summary>
    /// Builds the Anthropic Messages API URL for a provider base URL, using the same version-segment rule
    /// as <see cref="BuildModelsUrl"/> (a base ending in <c>/v1</c>, or containing <c>/v1beta</c> anywhere,
    /// gets <c>/messages</c> appended; anything else gets <c>/v1/messages</c>).
    /// </summary>
    /// <param name="baseUrl">The provider's configured base URL.</param>
    public static string BuildMessagesUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var hasVersionSegment = trimmed.EndsWith(value: "/v1", comparisonType: StringComparison.OrdinalIgnoreCase)
                                || trimmed.Contains(value: "/v1beta",
                                    comparisonType: StringComparison.OrdinalIgnoreCase);
        return hasVersionSegment ? $"{trimmed}/messages" : $"{trimmed}/v1/messages";
    }

    /// <summary>
    /// Strips a trailing OpenAI-compatibility version segment so a native-API probe hits the host root.
    /// </summary>
    /// <remarks>
    /// Load-bearing for exactly the providers native probing exists to serve. LM Studio and Ollama are
    /// normally configured with a <c>/v1</c> base - that is where their OpenAI-compatible routes live - but
    /// their native APIs sit at the root: <c>/api/v0/models</c>, <c>/api/tags</c>, <c>/api/show</c>.
    /// Probing <c>http://localhost:11434/v1/api/tags</c> would 404 on every Ollama install, recording the
    /// native flavor as absent and silently costing tier-1 template detection its best source.
    /// </remarks>
    /// <param name="baseUrl">The provider's configured base URL.</param>
    public static string StripVersionSuffix(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith(value: "/v1", comparisonType: StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^"/v1".Length]
            : trimmed;
    }

    /// <summary>
    /// Joins a passthrough provider's base URL with the client's own request path and query, collapsing
    /// any path segments the two both carry so a shared prefix is not emitted twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Passthrough routes have no translator, so the client's request path - not this codebase - names the
    /// upstream resource, and an OpenAI-shaped client always sends the full <c>/v1/chat/completions</c>.
    /// A base URL is therefore normally just an origin (<c>https://api.openai.com</c>), but two shapes
    /// legitimately carry a path: local runtimes documented with one (<c>http://localhost:11434/v1</c>,
    /// <c>http://127.0.0.1:1234/v1</c>) and providers reached through a gateway prefix
    /// (<c>https://gw.corp/openai</c>). Plain concatenation serves the second and breaks the first -
    /// <c>/v1</c> + <c>/v1/chat/completions</c> forwards to <c>/v1/v1/chat/completions</c>, which LM Studio
    /// answers with HTTP <em>200</em> and an error-shaped body, so nothing downstream (circuit breaker,
    /// telemetry, the client) can tell it went wrong. Combining via <see cref="Uri(Uri, string)"/> serves
    /// the first and breaks the second, since an ASP.NET Core path always starts with <c>/</c> and is thus
    /// an RFC 3986 §5.3 absolute-path reference that <em>replaces</em> the base's path outright.
    /// </para>
    /// <para>
    /// Overlap-collapsing serves both, and is the whole of the rule: keep every base segment, then append
    /// only the request segments the base did not already supply. It is a superset of the concatenation it
    /// replaces - identical output for a path-less base, and identical for a base whose path the request
    /// does not repeat - so the §4.1 preservation behavior stands unchanged.
    /// </para>
    /// <para>
    /// Segment comparison is <see cref="StringComparison.Ordinal"/>, not case-insensitive, and matching is
    /// anchored at the boundary between the two: only a base <em>suffix</em> that is a request
    /// <em>prefix</em> collapses. Both restrictions are deliberate. Collapsing case-insensitively would
    /// rewrite the client's path casing to the base's on a server that treats the two as distinct
    /// resources; matching unanchored would let a segment repeated deeper in the path swallow the segments
    /// before it.
    /// </para>
    /// </remarks>
    /// <param name="baseUrl">The passthrough provider's configured base URL.</param>
    /// <param name="requestPath">
    /// The client's request path, leading <c>/</c> included; <see langword="null"/> or empty both
    /// mean the client sent none.
    /// </param>
    /// <param name="queryString">
    /// The client's query string, leading <c>?</c> included; <see langword="null"/> or empty both
    /// mean the client sent none.
    /// </param>
    public static string BuildPassthroughUrl(Uri baseUrl, string? requestPath, string? queryString)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        // Normalized rather than rejected, because null is a value the caller legitimately holds: the
        // middleware passes `context.Request.Path` and `context.Request.QueryString`, and both of those
        // types carry a null `Value` when empty (a PathString fully consumed by UsePathBase, a request with
        // no query). Null here means "the client sent none", not a call-site bug, so the base URL alone is
        // the right answer. Left unhandled it was an NRE one line deeper than it looked: SplitSegments
        // tolerates null and leaves the overlap at 0, and SkipLeadingSegments then indexes the null.
        var path = requestPath ?? string.Empty;
        var query = queryString ?? string.Empty;

        var trimmedBase = baseUrl.ToString().TrimEnd('/');
        var baseSegments = SplitSegments(baseUrl.AbsolutePath);
        var requestSegments = SplitSegments(path);

        // Largest k where the base's last k segments are the request's first k. Descending, so the longest
        // shared prefix wins: a base of "/openai/v1" meeting "/openai/v1/chat/completions" collapses both
        // segments rather than stopping at the one-segment match it would also satisfy.
        var overlap = 0;
        for (var k = Math.Min(val1: baseSegments.Length, val2: requestSegments.Length); k > 0; k--)
            if (baseSegments.AsSpan(baseSegments.Length - k).SequenceEqual(requestSegments.AsSpan(0, length: k)))
            {
                overlap = k;
                break;
            }

        // Sliced out of the original string rather than rejoined from the split segments: the remainder is
        // then forwarded byte-for-byte, keeping the client's own escaping and any trailing slash it sent
        // (some upstreams route "/v1/models/" and "/v1/models" differently). With overlap 0 - the
        // path-less-base case, which is every provider that worked before this method existed - the slice
        // is the whole path and this is exactly the concatenation it replaces.
        return $"{trimmedBase}{SkipLeadingSegments(path: path, count: overlap)}{query}";
    }

    /// <summary>
    /// Returns <paramref name="path"/> with its first <paramref name="count"/> segments removed, leading <c>/</c> and
    /// all.
    /// </summary>
    private static string SkipLeadingSegments(string path, int count)
    {
        var index = 0;
        for (var skipped = 0; skipped < count; skipped++)
        {
            while (index < path.Length && path[index] == '/') index++;

            while (index < path.Length && path[index] != '/') index++;
        }

        return path[index..];
    }

    /// <summary>Splits a path into its non-empty segments, ignoring leading/trailing/duplicate slashes.</summary>
    /// <param name="path">The path to split; <see langword="null"/> or empty yields no segments.</param>
    private static string[] SplitSegments(string path)
    {
        return string.IsNullOrEmpty(path)
            ? []
            : path.Split('/', options: StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Returns whether <paramref name="baseUrl"/> is an unencrypted (<c>http://</c>) upstream reaching
    /// somewhere other than this machine - the D6b warning condition (web GUI migration plan Phase P7).
    /// </summary>
    /// <remarks>
    /// A loopback <c>http://</c> base (local Ollama on <c>:11434</c>, LM Studio on <c>:1234</c>) is
    /// deliberately <em>not</em> flagged: those servers only speak plain HTTP, traffic to them never
    /// leaves the machine, and warning on every default local install would train operators to ignore
    /// the warning rather than act on it. An unparsable <paramref name="baseUrl"/> is not flagged either
    /// - provider validation elsewhere in <see cref="Proxy.Management.ManagementFacade"/> is responsible
    /// for rejecting a malformed base URL; this classifier only judges shape it can actually parse.
    /// </remarks>
    /// <param name="baseUrl">The provider's configured base URL.</param>
    public static bool IsUnencryptedNonLoopbackUpstream(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, uriKind: UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(a: uri.Scheme, b: Uri.UriSchemeHttp, comparisonType: StringComparison.OrdinalIgnoreCase))
            return false;

        return !IsLoopbackHost(uri.Host);
    }

    /// <summary>
    /// Returns whether <paramref name="host"/> - a URI's host component, not a socket's remote address -
    /// names this machine: the literal <c>localhost</c> (case-insensitive), or an IPv4/IPv6 loopback
    /// address in any of the forms a provider base URL might carry (<c>127.0.0.1</c>, <c>::1</c>, a
    /// bracketed <c>[::1]</c> already stripped of its brackets by <see cref="Uri.Host"/>).
    /// </summary>
    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(a: host, b: "localhost", comparisonType: StringComparison.OrdinalIgnoreCase)) return true;

        return IPAddress.TryParse(ipString: host, address: out var address) && IPAddress.IsLoopback(address);
    }
}