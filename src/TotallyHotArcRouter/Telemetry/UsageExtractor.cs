using System.Text;
using TotallyHot.ArcRouter.Proxy;

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
    // Cached rather than rebuilt per call: SupportsNativeShape is a static helper ProxyMiddleware calls
    // once per response, independent of any DI-constructed UsageExtractor instance, so it needs its own
    // copy of the default table rather than reading an instance field.
    private static readonly IReadOnlyDictionary<string, ProviderRegistration> DefaultRegistrationsForStaticLookup = ProviderRegistrations.BuildDefault();

    private readonly IReadOnlyDictionary<string, ProviderRegistration> _providerRegistrations;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageExtractor"/> class.
    /// </summary>
    /// <param name="providerRegistrations">
    /// The provider dispatch table (<c>provider key -&gt; <see cref="ProviderRegistration"/></c>) that
    /// decides which parser shape a given provider's captured bytes are in. Defaults to
    /// <see cref="ProviderRegistrations.BuildDefault"/> when not supplied - the same table
    /// <c>ServiceCollectionExtensions</c> registers for DI - so direct construction (production fallback
    /// construction in <c>ProxyMiddleware</c>, or a test building this type on its own) still dispatches
    /// every known provider correctly. Re-keyed onto <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// regardless of the comparer the caller's own dictionary used, so <see cref="TryExtractUsage"/>'s
    /// documented case-insensitive lookup contract holds no matter what a caller supplies here - not
    /// just for the case-insensitive default table.
    /// </param>
    public UsageExtractor(IReadOnlyDictionary<string, ProviderRegistration>? providerRegistrations = null)
    {
        _providerRegistrations = providerRegistrations is null
            ? ProviderRegistrations.BuildDefault()
            : new Dictionary<string, ProviderRegistration>(providerRegistrations, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reports whether <paramref name="provider"/> has a registered parser for its own <b>native</b>
    /// response shape (as opposed to only being reachable via a translated OpenAI-shaped body). Used by
    /// <c>ProxyMiddleware</c> to decide whether it is worth capturing a second, pre-translation copy of a
    /// translated provider's response for a native telemetry tap
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4) - capturing native bytes for a provider
    /// with no native parser would just be wasted memory. Reads the same default provider dispatch table
    /// <see cref="ProviderRegistrations.BuildDefault"/> builds for DI, so this and the instance-level
    /// <see cref="TryExtractUsage"/> dispatch can never drift apart over which providers are native-shaped.
    /// </summary>
    /// <param name="provider">The provider key (e.g. <c>"anthropic"</c>), case-insensitive.</param>
    public static bool SupportsNativeShape(string provider) =>
        DefaultRegistrationsForStaticLookup.TryGetValue(provider, out var registration)
        && registration.UsageParserShape == UsageParserShape.Native;

    /// <inheritdoc />
    public bool TryExtractUsage(string provider, bool isStreaming, ReadOnlyMemory<byte> bufferedResponseBody, out UsageInfo usage)
    {
        usage = default;

        if (bufferedResponseBody.IsEmpty)
        {
            return false;
        }

        // Unknown/unsupported provider (e.g. alibaba, zhipu, moonshot, minimax): no registration, so no
        // parser shape to dispatch on. Fail gracefully rather than guessing at an unverified response shape.
        if (!_providerRegistrations.TryGetValue(provider, out var registration))
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

        return registration.UsageParserShape switch
        {
            UsageParserShape.OpenAiCompatible => isStreaming
                ? OpenAiUsageParser.TryExtractFromStreamingBuffer(text, out usage)
                : OpenAiUsageParser.TryExtractFromNonStreamingBody(text, out usage),
            UsageParserShape.Native => isStreaming
                ? AnthropicUsageParser.TryExtractFromStreamingBuffer(text, out usage)
                : AnthropicUsageParser.TryExtractFromNonStreamingBody(text, out usage),
            _ => false,
        };
    }
}
