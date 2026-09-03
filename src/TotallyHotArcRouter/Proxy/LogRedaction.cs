namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Shared helpers for placing client-originated or unbounded text into a log message safely. Extracted
/// from <see cref="ProxyMiddleware"/> so <see cref="LocalEndpointResponder"/> can apply the same rules
/// without duplicating them.
/// </summary>
internal static class LogRedaction
{
    /// <summary>Caps the length of a full request/response body before it is placed in a Debug-level log message, so an unbounded payload never floods a text log sink. Applied on top of <see cref="Sanitize"/>, never in place of it.</summary>
    private const int MaxLoggedBodyLength = 4000;

    /// <summary>
    /// Strips CR/LF from a value that originated from the client (request path, header names, JSON body
    /// keys, a client-supplied session id) before it's placed in a log message template. Without this, a
    /// crafted value could inject newlines into a text-rendering log sink and forge what looks like
    /// additional, fabricated log entries (CodeQL: "Log entries created from user input" / log forging,
    /// CWE-117). Chained <see cref="string.Replace(string, string)"/> calls directly on the tainted value
    /// - rather than e.g. a hand-rolled character loop - is the sanitizer shape CodeQL's data-flow
    /// analysis recognizes as breaking the taint path from source to sink.
    /// </summary>
    public static string Sanitize(string? value) =>
        value?.Replace("\r", " ").Replace("\n", " ") ?? string.Empty;

    /// <summary>Truncates an already-sanitized value to <see cref="MaxLoggedBodyLength"/>, appending a marker when it was cut.</summary>
    public static string Truncate(string value) =>
        value.Length <= MaxLoggedBodyLength
            ? value
            : string.Concat(value.AsSpan(0, MaxLoggedBodyLength), "...[truncated]");
}
