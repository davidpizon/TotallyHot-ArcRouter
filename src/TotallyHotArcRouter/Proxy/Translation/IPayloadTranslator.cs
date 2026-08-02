using Microsoft.AspNetCore.Http;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Translates one provider's native request/response payload shape to/from the OpenAI-compatible
/// shape TotallyHot.ArcRouter's proxy speaks by default (PLAN.md TODO 4's "Unified API Translation" pillar -
/// see <c>docs/router/unified-api-translation.md</c> for the full plan). A provider with no registered
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
}

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

