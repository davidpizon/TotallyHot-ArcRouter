using TotallyHot.ArcRouter.Proxy.Translation;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Turns one client request plus one chosen candidate into the <see cref="HttpRequestMessage"/> to send
/// upstream: the target URL, the (possibly translated) body, and the forwarded header set. Extracted
/// from <see cref="ProxyMiddleware.InvokeCoreAsync"/>'s candidate loop, where it was ~95 lines
/// interleaved with failover and response handling.
/// <para>
/// Every candidate attempt builds a fresh message - an <see cref="HttpRequestMessage"/> cannot be sent
/// twice - so this runs once per candidate, not once per request. It performs no I/O: the body is
/// already materialized by the time it is called, and translation is synchronous.
/// </para>
/// </summary>
internal static class UpstreamRequestBuilder
{
    /// <summary>
    /// Client request headers never forwarded upstream. <c>Host</c>, <c>Content-Type</c> and
    /// <c>Content-Length</c> are simply re-derived for the new message; the other two are load-bearing:
    /// <para>
    /// <c>Authorization</c> carries the client's inbound credential to the proxy itself (e.g. a
    /// placeholder token an IDE/BYOK client requires but never validates), not a credential for the
    /// upstream provider. It must never be forwarded as-is: for providers whose auth header is something
    /// else (e.g. Anthropic's <c>x-api-key</c>), forwarding it would send the client's bogus token
    /// upstream alongside the real injected credential, and some providers reject the request outright
    /// when both are present.
    /// </para>
    /// <para>
    /// <c>Accept-Encoding</c> is skipped because the shared <see cref="System.Net.Http.HttpClient"/> never
    /// configures <c>AutomaticDecompression</c>: if the client's own <c>Accept-Encoding: gzip</c> were
    /// relayed upstream, a provider that honors it would send a gzip-compressed body that this proxy reads
    /// (and, for a translated provider, re-parses as plain-text SSE/JSON) without ever decompressing it -
    /// producing either a translation failure or, worse, a successful-looking response whose
    /// <c>Content-Encoding</c> header (copied from upstream) is a lie once the body has been rewritten by
    /// a translator, which the client then fails to gunzip (zlib "incorrect header check"). Not asking
    /// upstream to compress at all sidesteps both failure modes.
    /// </para>
    /// </summary>
    internal static readonly string[] AlwaysSkippedRequestHeaders =
        ["Host", "Content-Type", "Content-Length", "Authorization", "Accept-Encoding"];

    /// <summary>
    /// Builds the upstream request for one candidate.
    /// </summary>
    /// <param name="context">The inbound client request, source of the method, path, query, and headers.</param>
    /// <param name="route">
    /// The candidate being attempted - supplies the base URL, upstream model id, auth-header
    /// configuration, and any provider-configured extra headers.
    /// </param>
    /// <param name="translator">The provider's translator, or <see langword="null"/> for a passthrough provider.</param>
    /// <param name="rewrittenBody">The request body after <c>RequestInterceptor</c>'s model rewrite, in OpenAI shape.</param>
    /// <returns>A fresh <see cref="HttpRequestMessage"/>. The caller owns it and must dispose it.</returns>
    internal static HttpRequestMessage Build(
        HttpContext context,
        ResolvedModelRoute route,
        IPayloadTranslator? translator,
        byte[] rewrittenBody)
    {
        var (targetUri, forwardBody) = ResolveTargetAndBody(context: context, route: route, translator: translator,
            rewrittenBody: rewrittenBody);

        var requestMessage = new HttpRequestMessage
        {
            RequestUri = targetUri,
            Method = new HttpMethod(context.Request.Method)
        };

        CopyClientHeaders(context: context, route: route, requestMessage: requestMessage);

        requestMessage.Content = new ByteArrayContent(forwardBody);
        requestMessage.Content.Headers.TryAddWithoutValidation(name: "Content-Type", value: "application/json");

        // Provider-configured custom headers (e.g. anthropic-version, and whichever header carries
        // authentication). Added only when the client didn't already send that header, so a client
        // supplying its own value keeps it rather than having it clobbered or duplicated. Client headers
        // were copied above - except the auth header, which was skipped there by name (and thus sourced
        // from here instead) only when the provider actually has one configured; for a provider with no
        // auth header configured, the client's own header of that name was left in place and nothing here
        // touches it.
        foreach (var (headerName, headerValue) in route.ExtraHeaders)
            if (!requestMessage.Headers.Contains(headerName))
                requestMessage.Headers.TryAddWithoutValidation(name: headerName, value: headerValue);

        return requestMessage;
    }

    /// <summary>
    /// Picks the upstream URL and the bytes to send. Reshaping the body and owning the upstream URL are
    /// two independent axes, not one: every translator before tool-call emulation did both or neither, but
    /// an <see cref="IClientPathTranslator"/> rewrites the body heavily while still addressing the same
    /// OpenAI-compatible endpoint on the client's own path, and an <see cref="IResponseOnlyTranslator"/>
    /// does neither on the request side while still being consulted for the response.
    /// </summary>
    private static (Uri TargetUri, byte[] ForwardBody) ResolveTargetAndBody(
        HttpContext context,
        ResolvedModelRoute route,
        IPayloadTranslator? translator,
        byte[] rewrittenBody)
    {
        // A response-only translator is treated like "no translator" for request-forwarding purposes,
        // while still being non-null for response handling (streaming/buffered dispatch,
        // Content-Length/Content-Encoding stripping) - see IResponseOnlyTranslator for why its request
        // side cannot be routed through BuildRequestUri.
        var isRequestReshapingTranslator = translator is not null and not IResponseOnlyTranslator;
        var buildsOwnRequestUri = isRequestReshapingTranslator && translator is not IClientPathTranslator;

        if (buildsOwnRequestUri)
        {
            var requestIsStreaming = ProxyMiddleware.IsStreamingRequest(rewrittenBody);
            return (
                translator!.BuildRequestUri(baseUrl: route.UpstreamBaseUrl, providerModelId: route.ProviderModelId,
                    isStreaming: requestIsStreaming),
                translator.TranslateRequest(rewrittenBody));
        }

        // Deliberately neither `new Uri(route.UpstreamBaseUrl, relativePath)` nor plain string
        // concatenation - both are lossy in opposite directions, and BuildPassthroughUrl exists to be
        // neither. Combining drops a BaseUrl's own path (an ASP.NET Core path always starts with "/",
        // making it an absolute-path reference that *replaces* the base's path); concatenating emits a
        // shared prefix twice, so an LM Studio base of "http://127.0.0.1:1234/v1" meeting a client's
        // "/v1/chat/completions" forwards to "/v1/v1/chat/completions" - which LM Studio answers 200 with
        // an error body, so it fails silently everywhere downstream. See
        // ProviderUrlBuilder.BuildPassthroughUrl and src/README.md's "Provider base URLs".
        // `.Value` on both rather than PathString's implicit string conversion and QueryString.ToString().
        // The two disagree about empty - `Value` is null, ToString() is "" - and BuildPassthroughUrl
        // accepts either, so the choice is about which one says so. The explicit nullable spelling is the
        // one that matches the documented contract; going through ToString() would quietly guarantee
        // non-null here and make the null handling on the other side look like dead defensive code the
        // next reader deletes.
        var passthroughUri = new Uri(ProviderUrlBuilder.BuildPassthroughUrl(
            baseUrl: route.UpstreamBaseUrl,
            requestPath: context.Request.Path.Value,
            queryString: context.Request.QueryString.Value));

        // Still consulted for the body, so a client-path translator gets its rewrite. A response-only
        // translator's TranslateRequest is the identity by contract, which keeps this the byte-for-byte
        // forwarding path it has always been for everything else.
        return (
            passthroughUri,
            isRequestReshapingTranslator ? translator!.TranslateRequest(rewrittenBody) : rewrittenBody);
    }

    /// <summary>
    /// Copies the client's own headers onto the upstream message, skipping the ones that must never be
    /// relayed (<see cref="AlwaysSkippedRequestHeaders"/>), the hop-by-hop set nominated by this request's
    /// <c>Connection</c> header, and - conditionally - the provider's configured auth header.
    /// </summary>
    private static void CopyClientHeaders(HttpContext context, ResolvedModelRoute route,
        HttpRequestMessage requestMessage)
    {
        var requestHopByHopHeaders = ProxyMiddleware.GetHopByHopHeaderNames(
            context.Request.Headers.TryGetValue(key: "Connection", value: out var requestConnectionValues)
                ? requestConnectionValues
                : default);

        // A client header matching the provider's configured auth header name is only skipped when the
        // provider's configuration actually declares one - otherwise an unauthenticated provider (e.g. a
        // free local runtime with no auth header entry) would silently drop a client's own header of that
        // name with nothing forwarded in its place. This is deliberately based on configuration intent
        // rather than whether the header resolved into route.ExtraHeaders *this request*: a provider whose
        // credential env var is temporarily unset must still have the client's own header stripped
        // (failing closed with no credential forwarded), not let the client's header through as a stand-in
        // for the operator-configured one.
        var providerSuppliesAuthHeader = route.AuthHeaderConfigured;

        foreach (var header in context.Request.Headers)
        {
            if (AlwaysSkippedRequestHeaders.Contains(value: header.Key, comparer: StringComparer.OrdinalIgnoreCase) ||
                requestHopByHopHeaders.Contains(header.Key) ||
                (providerSuppliesAuthHeader && string.Equals(a: header.Key, b: route.AuthHeaderName,
                    comparisonType: StringComparison.OrdinalIgnoreCase)))
                continue;

            requestMessage.Headers.TryAddWithoutValidation(name: header.Key, values: [.. header.Value]);
        }
    }
}