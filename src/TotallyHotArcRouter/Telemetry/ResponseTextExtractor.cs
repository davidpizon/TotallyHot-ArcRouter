using System.Text;
using TotallyHot.ArcRouter.Proxy;

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
    /// <param name="provider">The provider key the request was routed to (e.g. <c>"openai"</c>, <c>"anthropic"</c>), case-insensitive.</param>
    /// <param name="isStreaming">Whether the response was a streaming (SSE) response.</param>
    /// <param name="bufferedResponseBody">The captured response bytes (may be truncated for very large responses - see the capture-cap note on the caller).</param>
    /// <param name="text">The extracted text, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if text could be determined; otherwise <see langword="false"/> (unknown provider, malformed/truncated body, or no text content in the response).</returns>
    bool TryExtractText(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out string text);
}

/// <inheritdoc cref="IResponseTextExtractor" />
public sealed class ResponseTextExtractor : IResponseTextExtractor
{
    private readonly IReadOnlyDictionary<string, ProviderRegistration> _providerRegistrations;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResponseTextExtractor"/> class.
    /// </summary>
    /// <param name="providerRegistrations">
    /// The provider dispatch table (<c>provider key -&gt; <see cref="ProviderRegistration"/></c>) that
    /// decides which parser shape a given provider's captured bytes are in. Defaults to
    /// <see cref="ProviderRegistrations.BuildDefault"/> when not supplied - the same table
    /// <c>ServiceCollectionExtensions</c> registers for DI, and the same one <see cref="UsageExtractor"/>
    /// falls back to - so direct construction (production fallback construction in
    /// <c>ProxyMiddleware</c>, or a test building this type on its own) still dispatches every known
    /// provider correctly, and the two extractors can never disagree about a given provider's shape.
    /// Re-keyed onto <see cref="StringComparer.OrdinalIgnoreCase"/> regardless of the comparer the
    /// caller's own dictionary used, so <see cref="TryExtractText"/>'s documented case-insensitive
    /// lookup contract holds no matter what a caller supplies here - not just for the case-insensitive
    /// default table.
    /// </param>
    public ResponseTextExtractor(IReadOnlyDictionary<string, ProviderRegistration>? providerRegistrations = null)
    {
        _providerRegistrations = providerRegistrations is null
            ? ProviderRegistrations.BuildDefault()
            : new Dictionary<string, ProviderRegistration>(providerRegistrations, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool TryExtractText(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out string text)
    {
        text = string.Empty;

        if (bufferedResponseBody.IsEmpty)
        {
            return false;
        }

        // Unknown/unsupported provider (e.g. alibaba, zhipu, moonshot, minimax), or a null/blank key: no
        // registration, so no parser shape to dispatch on. Fail gracefully rather than guessing at an
        // unverified response shape, or throwing on a Dictionary null-key lookup for a public method.
        if (string.IsNullOrEmpty(provider) || !_providerRegistrations.TryGetValue(provider, out var registration))
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

        return registration.UsageParserShape switch
        {
            UsageParserShape.OpenAiCompatible => isStreaming
                ? OpenAiResponseTextParser.TryExtractFromStreamingBuffer(body, out text)
                : OpenAiResponseTextParser.TryExtractFromNonStreamingBody(body, out text),
            UsageParserShape.Native => isStreaming
                ? AnthropicResponseTextParser.TryExtractFromStreamingBuffer(body, out text)
                : AnthropicResponseTextParser.TryExtractFromNonStreamingBody(body, out text),
            _ => false,
        };
    }
}
