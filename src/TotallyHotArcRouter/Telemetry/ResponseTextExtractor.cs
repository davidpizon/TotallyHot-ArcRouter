using System.Text;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Extracts the assistant's reply text from an already-fully-buffered upstream response body,
/// dispatching to the right provider-specific parser. Mirrors <see cref="IUsageExtractor"/>'s
/// single-shot-parse-over-a-captured-buffer design exactly, for <see cref="RoutingTelemetryEvent.ResponseSummary"/>.
/// Also reports whether that body carried a tool-call block so the transcript row can label a no-text reply
/// (#176 Phase 2).
/// </summary>
public interface IResponseTextExtractor
{
    /// <summary>
    /// Attempts to extract the assistant's reply text for a completed request.
    /// </summary>
    /// <param name="provider">
    /// The provider key the request was routed to (e.g. <c>"openai"</c>, <c>"anthropic"</c>),
    /// case-insensitive.
    /// </param>
    /// <param name="isStreaming">Whether the response was a streaming (SSE) response.</param>
    /// <param name="bufferedResponseBody">
    /// The captured response bytes (may be truncated for very large responses - see the
    /// capture-cap note on the caller).
    /// </param>
    /// <param name="text">The extracted text, when this method returns <see langword="true"/>.</param>
    /// <param name="hasToolCalls">
    /// <see langword="true"/> when a tool-call block was seen, <see langword="false"/> when the body was
    /// parseable and had none, or <see langword="null"/> when the provider is unregistered or the body could
    /// not be read. Independent of whether text was found: a tool-only reply returns
    /// <see langword="false"/> from this method with <paramref name="hasToolCalls"/> still
    /// <see langword="true"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if text could be determined; otherwise <see langword="false"/> (unknown provider,
    /// malformed/truncated body, or no non-blank text in the response). Text is returned verbatim: whitespace
    /// inside the reply, including whitespace-only stream deltas, is kept.
    /// </returns>
    bool TryExtractText(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out string text,
        out bool? hasToolCalls);
}

/// <inheritdoc cref="IResponseTextExtractor"/>
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
            : new Dictionary<string, ProviderRegistration>(collection: providerRegistrations,
                comparer: StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public bool TryExtractText(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody,
        out string text, out bool? hasToolCalls)
    {
        text = string.Empty;
        hasToolCalls = null;

        if (bufferedResponseBody.IsEmpty) return false;

        // Unknown/unsupported provider (e.g. alibaba, zhipu, moonshot, minimax), or a null/blank key: no
        // registration, so no parser shape to dispatch on. Fail gracefully rather than guessing at an
        // unverified response shape, or throwing on a Dictionary null-key lookup for a public method.
        // hasToolCalls stays null so the transcript row records "unknown", never a false negative.
        if (string.IsNullOrEmpty(provider) ||
            !_providerRegistrations.TryGetValue(key: provider, value: out var registration)) return false;

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
                ? OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sseText: body, text: out text,
                    hasToolCalls: out hasToolCalls)
                : OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json: body, text: out text,
                    hasToolCalls: out hasToolCalls),
            UsageParserShape.Native => isStreaming
                ? AnthropicResponseTextParser.TryExtractFromStreamingBuffer(sseText: body, text: out text,
                    hasToolCalls: out hasToolCalls)
                : AnthropicResponseTextParser.TryExtractFromNonStreamingBody(json: body, text: out text,
                    hasToolCalls: out hasToolCalls),
            _ => false
        };
    }
}
