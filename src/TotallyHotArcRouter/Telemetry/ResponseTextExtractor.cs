using System.Text;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts the assistant's reply text from an already-fully-buffered upstream response body,
/// dispatching to the right provider-specific parser. Mirrors <see cref="IUsageExtractor"/>'s
/// single-shot-parse-over-a-captured-buffer design exactly, for <see cref="RoutingTelemetryEvent.ResponseSummary"/>.
/// </summary>
public interface IResponseTextExtractor
{
    /// <summary>
    /// Attempts to extract the assistant's reply text for a completed request.
    /// </summary>
    /// <param name="provider">The provider key the request was routed to (e.g. <c>"openai"</c>, <c>"anthropic"</c>).</param>
    /// <param name="isStreaming">Whether the response was a streaming (SSE) response.</param>
    /// <param name="bufferedResponseBody">The captured response bytes (may be truncated for very large responses - see the capture-cap note on the caller).</param>
    /// <param name="text">The extracted text, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if text could be determined; otherwise <see langword="false"/> (unknown provider, malformed/truncated body, or no text content in the response).</returns>
    bool TryExtractText(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out string text);
}

/// <inheritdoc cref="IResponseTextExtractor" />
public sealed class ResponseTextExtractor : IResponseTextExtractor
{
    /// <inheritdoc />
    public bool TryExtractText(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out string text)
    {
        text = string.Empty;

        if (bufferedResponseBody.IsEmpty)
        {
            return false;
        }

        string body;
        try
        {
            body = Encoding.UTF8.GetString(bufferedResponseBody.Span);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return provider.ToLowerInvariant() switch
        {
            // "gemini" is OpenAI-shaped by this point - ProxyMiddleware translates the Gemini response
            // to OpenAI's choices[] shape before capturing it (docs/router/unified-api-translation.md §4.3).
            "openai" or "gemini" => isStreaming
                ? OpenAiResponseTextParser.TryExtractFromStreamingBuffer(body, out text)
                : OpenAiResponseTextParser.TryExtractFromNonStreamingBody(body, out text),
            "anthropic" => isStreaming
                ? AnthropicResponseTextParser.TryExtractFromStreamingBuffer(body, out text)
                : AnthropicResponseTextParser.TryExtractFromNonStreamingBody(body, out text),
            // Unknown/unsupported provider (e.g. alibaba, zhipu, moonshot, minimax): no parser wired
            // up yet. Fail gracefully rather than guessing at an unverified response shape.
            _ => false,
        };
    }
}

