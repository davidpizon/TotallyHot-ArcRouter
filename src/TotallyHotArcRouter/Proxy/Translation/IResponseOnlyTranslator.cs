namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Marks an <see cref="IPayloadTranslator"/> that rewrites only the <em>response</em>, leaving the
/// request exactly as <see cref="ProxyMiddleware"/> would have forwarded it with no translator at all.
/// <para>
/// The distinction is load-bearing rather than cosmetic. A real translator owns the upstream URL, because
/// a native provider encodes the model id and the streaming choice in the path
/// (<see cref="GeminiPayloadTranslator"/>). A response-only translator cannot:
/// <see cref="IPayloadTranslator.BuildRequestUri"/>
/// has no access to the client's own request path, which an OpenAI-shaped passthrough provider
/// (Ollama, LM Studio, ...) still needs preserved - so routing one through the request-reshaping path
/// would silently drop it. <see cref="ProxyMiddleware"/> therefore treats an implementer as "no
/// translator" for request forwarding while still treating it as "not null" for response handling
/// (streaming/buffered dispatch, <c>Content-Length</c>/<c>Content-Encoding</c> stripping).
/// </para>
/// <para>
/// A marker interface rather than a reference comparison against the middleware's own instance: any
/// implementer gets this treatment, not just the one the middleware happens to construct, so an
/// instance supplied through the provider-keyed translators dictionary cannot accidentally take the
/// request-reshaping path.
/// </para>
/// </summary>
internal interface IResponseOnlyTranslator : IPayloadTranslator
{
}