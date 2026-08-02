using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProviderUrlBuilder.BuildPassthroughUrl"/> - the single place the passthrough
/// forwarding path decides what a provider's configured base URL plus the client's own request path
/// add up to. See <c>src/README.md</c> ("Provider base URLs") for the contract these cases encode and
/// <c>docs/router/unified-api-translation.md</c> §4.1 for the history.
/// </summary>
public class ProviderUrlBuilderTests
{
    [Theory]
    // The bug this method was extracted to fix: a local runtime documented with a "/v1" base, meeting the
    // "/v1/chat/completions" every OpenAI-shaped client actually sends. Plain concatenation produced
    // "/v1/v1/chat/completions", which LM Studio answers 200 with an error-shaped body - a failure the
    // circuit breaker, telemetry, and the client are all structurally unable to notice.
    [InlineData("http://127.0.0.1:1234/v1", "/v1/chat/completions", "", "http://127.0.0.1:1234/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "/v1/chat/completions", "", "http://localhost:11434/v1/chat/completions")]
    // §4.1's preservation behavior, unchanged: a base path the request does not repeat still survives.
    [InlineData("http://localhost:11434/v1", "/chat/completions", "", "http://localhost:11434/v1/chat/completions")]
    // A path-less base - every provider that predates any of this. Byte-identical to concatenation.
    [InlineData("https://api.openai.com", "/v1/chat/completions", "", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://example.com", "/chat", "?x=1", "https://example.com/chat?x=1")]
    // A trailing slash on the base is not a third contract; it means the same thing as its absence.
    [InlineData("https://api.openai.com/", "/v1/chat/completions", "", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "/v1/chat/completions", "", "http://localhost:11434/v1/chat/completions")]
    // A gateway prefix: the reason plain Uri(Uri, string) combining cannot come back. "/openai" is not
    // repeated by the client and must survive; the shared "/v1" must still collapse.
    [InlineData("https://gw.corp/openai", "/v1/chat/completions", "", "https://gw.corp/openai/v1/chat/completions")]
    [InlineData("https://gw.corp/openai/v1", "/v1/chat/completions", "", "https://gw.corp/openai/v1/chat/completions")]
    // Multi-segment overlap collapses whole, not just its last segment.
    [InlineData("https://gw.corp/openai/v1", "/openai/v1/chat/completions", "", "https://gw.corp/openai/v1/chat/completions")]
    // Overlap is anchored at the join, so a segment repeated deeper in the path cannot swallow what
    // precedes it: "/models" here is a request segment that also ends the base, but not at the boundary.
    [InlineData("https://gw.corp/models", "/v1/models/list", "", "https://gw.corp/models/v1/models/list")]
    // Ordinal comparison: "/V1" and "/v1" are not assumed to be the same resource, so nothing collapses
    // and the client's own casing is forwarded untouched rather than rewritten to the base's.
    [InlineData("http://127.0.0.1:1234/V1", "/v1/chat/completions", "", "http://127.0.0.1:1234/V1/v1/chat/completions")]
    // A near-miss is not an overlap: "/v1" does not prefix "/v1beta".
    [InlineData("https://generativelanguage.googleapis.com/v1", "/v1beta/models", "", "https://generativelanguage.googleapis.com/v1/v1beta/models")]
    // A trailing slash the client sent is forwarded as sent - some upstreams route the two differently.
    [InlineData("http://localhost:11434/v1", "/v1/models/", "", "http://localhost:11434/v1/models/")]
    // Degenerate paths: the request adds nothing beyond what the base already names.
    [InlineData("http://localhost:11434/v1", "/v1", "", "http://localhost:11434/v1")]
    [InlineData("https://api.openai.com", "", "", "https://api.openai.com")]
    [InlineData("https://api.openai.com", "/", "", "https://api.openai.com/")]
    // The query string is carried through verbatim, including when the path fully collapses.
    [InlineData("http://localhost:11434/v1", "/v1/models", "?limit=5", "http://localhost:11434/v1/models?limit=5")]
    // Null is the same input as empty, not an error. PathString and QueryString both expose a null `Value`
    // when they are empty, and the middleware hands those to this method directly, so a request whose path
    // was fully consumed by a path base arrives here as null and must still forward to the base URL.
    [InlineData("http://localhost:11434/v1", null, "", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/v1", "/v1/models", null, "http://localhost:11434/v1/models")]
    [InlineData("http://localhost:11434/v1", null, null, "http://localhost:11434/v1")]
    public void BuildPassthroughUrl_JoinsBaseAndRequestPath_CollapsingOnlyTheSharedBoundarySegments(
        string baseUrl,
        string? requestPath,
        string? queryString,
        string expected)
    {
        Assert.Equal(expected, ProviderUrlBuilder.BuildPassthroughUrl(new Uri(baseUrl), requestPath, queryString));
    }
}

