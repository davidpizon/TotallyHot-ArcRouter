using System.Text;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts token usage from an already-fully-buffered upstream response body, dispatching to the
/// right provider-specific parser. This is a single-shot parse over a captured byte buffer (see
/// <c>ProxyMiddleware</c>'s response tap), not an incremental/live SSE parser - simpler and lower-risk
/// than parsing each chunk as it arrives, at the cost of only knowing usage once the response (or the
/// capture cap) is reached.
/// </summary>
public interface IUsageExtractor
{
    /// <summary>
    /// Attempts to extract token usage for a completed request.
    /// </summary>
    /// <param name="provider">The provider key the request was routed to (e.g. <c>"openai"</c>, <c>"ollama"</c>, <c>"anthropic"</c>).</param>
    /// <param name="isStreaming">Whether the response was a streaming (SSE) response.</param>
    /// <param name="bufferedResponseBody">The captured response bytes (see the capture-cap note on the caller - may be truncated for very large responses).</param>
    /// <param name="usage">The extracted usage, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if usage could be determined; otherwise <see langword="false"/> (unknown provider, malformed/truncated body, or the provider's response genuinely omitted usage).</returns>
    bool TryExtractUsage(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out UsageInfo usage);
}

/// <inheritdoc cref="IUsageExtractor" />
public sealed class UsageExtractor : IUsageExtractor
{
    /// <summary>
    /// Reports whether <paramref name="provider"/> has a registered parser for its own <b>native</b>
    /// response shape (as opposed to only being reachable via a translated OpenAI-shaped body). Used by
    /// <c>ProxyMiddleware</c> to decide whether it is worth capturing a second, pre-translation copy of a
    /// translated provider's response for a native telemetry tap
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4) - capturing native bytes for a provider
    /// with no native parser would just be wasted memory. A single source of truth here, rather than a
    /// duplicated string check in the middleware, is what keeps the two from drifting apart.
    /// </summary>
    /// <param name="provider">The provider key (e.g. <c>"anthropic"</c>), case-insensitive.</param>
    public static bool SupportsNativeShape(string provider) =>
        string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool TryExtractUsage(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out UsageInfo usage)
    {
        usage = default;

        if (bufferedResponseBody.IsEmpty)
        {
            return false;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(bufferedResponseBody.Span);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return provider.ToLowerInvariant() switch
        {
            // Ollama's OpenAI-compatible routes answer in OpenAI's own response shape - same
            // choices[].message + usage.prompt_tokens/completion_tokens, same SSE framing (see
            // docs/router/unified-api-translation.md §4.1, pinned by OllamaProviderTests) - so it shares
            // the parser rather than getting a duplicate of it. This is a verified shape, not a guess at
            // one, which is what separates it from the unsupported providers below.
            // "gemini" is OpenAI-shaped by the time usage is parsed: ProxyMiddleware runs the Gemini
            // response/stream through GeminiPayloadTranslator before capturing it, so the captured bytes
            // are already OpenAI's choices[]/usage shape (see docs/router/unified-api-translation.md §4.3).
            "openai" or "ollama" or "gemini" => isStreaming
                ? OpenAiUsageParser.TryExtractFromStreamingBuffer(text, out usage)
                : OpenAiUsageParser.TryExtractFromNonStreamingBody(text, out usage),
            "anthropic" => isStreaming
                ? AnthropicUsageParser.TryExtractFromStreamingBuffer(text, out usage)
                : AnthropicUsageParser.TryExtractFromNonStreamingBody(text, out usage),
            // Unknown/unsupported provider (e.g. alibaba, zhipu, moonshot, minimax): no parser wired
            // up yet. Fail gracefully rather than guessing at an unverified response shape.
            _ => false,
        };
    }
}

