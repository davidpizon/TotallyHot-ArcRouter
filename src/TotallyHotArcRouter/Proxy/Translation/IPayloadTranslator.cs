namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Translates one provider's native request/response payload shape to/from the OpenAI-compatible
/// shape TotallyHot.ArcRouter's proxy speaks by default (see
/// <c>docs/router/unified-api-translation.md</c> for the full plan). A provider with no registered
/// translator (openai, ollama today) is forwarded unchanged, exactly as <see cref="ProxyMiddleware"/>
/// already does; this interface is consulted only for providers whose native shape actually differs
/// from OpenAI's.
///
/// <para>
/// The seam grew beyond a pure body-in/body-out translator when the first real implementation
/// (Google Gemini, §4.3) landed: Gemini's native API also puts the model id and the streaming choice
/// in the <em>URL path</em> (<c>:generateContent</c> vs <c>:streamGenerateContent?alt=sse</c>), not just
/// the body, and its streaming shape needs per-request state (accumulation across fragmented chunks,
/// tool-call index continuity) - so <see cref="BuildRequestUri"/> and
/// <see cref="CreateStreamTranslator"/> were added. This deviation from the interface originally
/// sketched in the design doc is recorded there, the same way §4.1 recorded the URL-combining bug it
/// discovered.
/// </para>
///
/// <para>
/// The Anthropic retrofit (§4.4) grew it again: "anthropic" is registered here, but unlike every other
/// translated provider it does not always translate - see <see cref="ShouldTranslate"/>.
/// </para>
/// </summary>
public interface IPayloadTranslator
{
    /// <summary>The provider key this translator applies to (matches <see cref="TotallyHot.ArcRouter.Models.ModelRouteEntry.Provider"/>).</summary>
    string Provider { get; }

    /// <summary>
    /// Whether this specific request should be translated at all. Every translator prior to the
    /// Anthropic retrofit (§4.4) always translates - a provider registered here has exactly one native
    /// shape. Anthropic is the first exception: the same "anthropic" provider key now serves both an
    /// OpenAI-shaped client (needs translating to Anthropic's Messages API) and a client that already
    /// speaks Anthropic natively - real Claude Code production traffic, which must keep passing through
    /// byte-for-byte exactly as it did before this translator existed. The default implementation
    /// preserves that "always translate" behavior for every other translator without requiring them to
    /// implement this member.
    /// </summary>
    /// <param name="request">The inbound client request (path, headers, etc.) - not yet body-parsed.</param>
    bool ShouldTranslate(HttpRequest request) => true;

    /// <summary>
    /// Builds the absolute upstream URL to forward to. Unlike an OpenAI-shaped provider (where the
    /// forwarded URL is just <c>baseUrl</c> + the client's request path), a native provider may encode
    /// the model id and the streaming choice in the path itself - e.g. Gemini's
    /// <c>{baseUrl}/v1beta/models/{providerModelId}:generateContent</c>, or
    /// <c>:streamGenerateContent?alt=sse</c> when <paramref name="isStreaming"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="baseUrl">The provider's configured base URL (host, optionally with a path prefix).</param>
    /// <param name="providerModelId">The upstream model id (already resolved from the client-facing name).</param>
    /// <param name="isStreaming">Whether the client requested a streaming response (from the request body's <c>stream</c> field).</param>
    Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming);

    /// <summary>
    /// Rewrites an OpenAI-shaped request body into this provider's native shape. The same native body
    /// is used for both streaming and non-streaming requests - the streaming choice lives in the URL
    /// (<see cref="BuildRequestUri"/>), so any OpenAI <c>stream</c> field is dropped here rather than
    /// forwarded.
    /// </summary>
    byte[] TranslateRequest(byte[] openAiShapedBody);

    /// <summary>Rewrites this provider's native (non-streaming) response body into OpenAI's shape.</summary>
    byte[] TranslateResponse(byte[] nativeShapedBody);

    /// <summary>
    /// Creates a fresh, per-request <see cref="IStreamTranslator"/> for translating a streaming (SSE)
    /// response. A new instance is required per request because streaming translation is stateful
    /// (buffered accumulation of fragmented chunks, tool-call index continuity across chunks).
    /// </summary>
    IStreamTranslator CreateStreamTranslator();

    /// <summary>
    /// Whether an upstream response with this status code carries a provider error envelope worth
    /// decoding before anything is written to the client. Returning <see langword="true"/> makes
    /// <see cref="ProxyMiddleware"/> buffer the (small) error body and hand it to
    /// <see cref="TryExtractEmbeddedError"/>; the default <see langword="false"/> leaves the response
    /// on the untouched forwarding path.
    ///
    /// <para>
    /// This is a separate decision from <see cref="TryExtractEmbeddedError"/> because the buffering has
    /// to happen <em>before</em> there are any bytes to parse, and buffering is observable: a pre-read
    /// body is forwarded whole rather than streamed, and it is what ADR-0004's out-of-credits
    /// classifier inspects. A translator must therefore opt in only for the statuses whose bodies it
    /// genuinely knows how to read, rather than for every error status.
    /// </para>
    /// </summary>
    /// <param name="statusCode">The upstream response's HTTP status code.</param>
    bool HandlesEmbeddedErrorAt(int statusCode) => false;

    /// <summary>
    /// Extracts an error this provider embedded in a response body whose shape
    /// <see cref="TranslateResponse"/> would otherwise mangle into a bogus empty completion (or, on the
    /// SSE path, silently swallow). Called only when <see cref="HandlesEmbeddedErrorAt"/> returned
    /// <see langword="true"/> for the same status code.
    ///
    /// <para>
    /// This member is what keeps provider-specific error decoding out of <see cref="ProxyMiddleware"/>:
    /// before it existed, the middleware type-tested each concrete translator class and called a
    /// <see langword="static"/> extractor on it, so a newly added translated provider was silently
    /// un-classified until someone remembered to extend that chain. The default returns
    /// <see langword="false"/>, which is the correct behavior for a provider whose errors need no
    /// special handling - its body is forwarded unchanged, exactly as before.
    /// </para>
    /// </summary>
    /// <param name="body">The buffered upstream error body.</param>
    /// <param name="error">The decoded error when this returns <see langword="true"/>; otherwise <see langword="default"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="body"/> parsed as this provider's error envelope.</returns>
    bool TryExtractEmbeddedError(byte[] body, out EmbeddedProviderError error)
    {
        error = default;
        return false;
    }
}

/// <summary>
/// One error a provider embedded in a response body, as decoded by
/// <see cref="IPayloadTranslator.TryExtractEmbeddedError"/>. A struct rather than a tuple of
/// <see langword="out"/> parameters so that <see cref="IsAuthFailure"/> - the one piece of this that is
/// genuinely provider-specific judgement rather than transcription - travels with the data instead of
/// being re-derived by every caller.
/// </summary>
/// <param name="Status">The provider's own error status/type token (Gemini's <c>error.status</c>, Anthropic's <c>error.type</c>), or empty when the envelope carried none.</param>
/// <param name="Message">The human-readable error message. Never empty when extraction succeeded.</param>
/// <param name="IsAuthFailure">
/// Whether this provider considers the error a credential failure disguised as a non-401 status. Gemini
/// is the case this exists for: it reports an invalid API key as a 400 carrying
/// <c>UNAUTHENTICATED</c>, which the circuit breaker must treat as the provider-wide outage a real 401
/// would be (see <c>docs/adr/0004</c>/<c>0005</c>) rather than as a per-request client fault.
/// </param>
public readonly record struct EmbeddedProviderError(string Status, string Message, bool IsAuthFailure);

/// <summary>
/// Per-request, stateful translator for one streaming response: consumes the upstream provider's
/// native SSE bytes as they arrive and emits OpenAI-shaped <c>chat.completion.chunk</c> SSE bytes to
/// forward to the client. Not thread-safe and not reusable across requests - obtain one from
/// <see cref="IPayloadTranslator.CreateStreamTranslator"/> per response.
///
/// <para>
/// Streaming-error semantics mirror LiteLLM's Gemini iterator (the parity reference): an upstream
/// chunk carrying an embedded provider error (e.g. a 429 <c>RESOURCE_EXHAUSTED</c> delivered as an
/// HTTP 200 SSE body with an <c>error</c> field) terminates the stream by throwing, rather than being
/// dropped or forwarded raw; a fragmented/partial JSON chunk is accumulated across pushes until it
/// parses, rather than being dropped; and a metadata-only or finish-only chunk still emits so a
/// <c>finish_reason</c> is never lost.
/// </para>
/// </summary>
public interface IStreamTranslator
{
    /// <summary>
    /// Feeds the next chunk of raw upstream SSE bytes and returns the OpenAI-shaped SSE bytes to write
    /// to the client now. May return an empty array while an event is still being accumulated, or emit
    /// several translated events at once. Throws to terminate the stream on an embedded provider error.
    /// </summary>
    byte[] Push(ReadOnlySpan<byte> upstreamChunk);

    /// <summary>
    /// Called once after the upstream stream ends: flushes any buffered/accumulated event and appends
    /// the terminal <c>data: [DONE]</c> line, returning the final OpenAI-shaped SSE bytes to write.
    /// </summary>
    byte[] Flush();
}

