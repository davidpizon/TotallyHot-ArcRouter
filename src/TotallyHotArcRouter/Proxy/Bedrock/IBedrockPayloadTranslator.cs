using TotallyHot.ArcRouter.Proxy.Translation;

namespace TotallyHot.ArcRouter.Proxy.Bedrock;

/// <summary>
/// Marks an <see cref="IPayloadTranslator"/> that targets an Amazon Bedrock model family (PLAN.md
/// TODO 4's "Unified API Translation" pillar, Bedrock slice - see
/// <c>docs/router/unified-api-translation.md</c> §4.2). Bedrock is invoked through the AWS SDK
/// (<c>IAmazonBedrockRuntime</c>), not a forwarded <c>HttpRequestMessage</c> - the SDK computes the
/// endpoint, performs SigV4 signing, and (for streaming) decodes AWS's binary event-stream framing
/// itself, so none of that is this codebase's concern. <see cref="ProxyMiddleware"/> uses this marker
/// to fork into a dedicated SDK-based invocation path instead of the raw-HTTP forwarding path every
/// other translated provider (Gemini, Anthropic) uses; <see cref="IPayloadTranslator.BuildRequestUri"/>
/// and <see cref="IPayloadTranslator.CreateStreamTranslator"/> are never called for a Bedrock
/// translator and throw <see cref="NotSupportedException"/> if they ever are, since that would mean
/// the dispatch fork failed to trigger.
/// </summary>
public interface IBedrockPayloadTranslator : IPayloadTranslator
{
    /// <summary>
    /// Creates a fresh, per-request stream-chunk translator for a Bedrock
    /// <c>InvokeModelWithResponseStream</c> response. Unlike <see cref="IStreamTranslator"/> (built for
    /// raw SSE bytes needing event-boundary buffering), each chunk here is already a complete,
    /// individually-decoded native JSON object - the AWS SDK has already parsed AWS's binary
    /// <c>application/vnd.amazon.eventstream</c> framing before handing it to this codebase, so there is
    /// no byte-level framing left to buffer or reassemble.
    /// </summary>
    IBedrockStreamChunkTranslator CreateBedrockStreamChunkTranslator();
}

/// <summary>
/// Per-request, stateful translator for one Bedrock <c>InvokeModelWithResponseStream</c> response:
/// consumes each already-decoded native JSON chunk the AWS SDK yields and emits OpenAI-shaped
/// <c>chat.completion.chunk</c> SSE bytes to forward to the client. Not thread-safe and not reusable
/// across requests - obtain one from <see cref="IBedrockPayloadTranslator.CreateBedrockStreamChunkTranslator"/>
/// per response.
/// </summary>
public interface IBedrockStreamChunkTranslator
{
    /// <summary>
    /// Translates one complete native JSON chunk (a single AWS SDK stream item's decoded payload bytes)
    /// into the OpenAI-shaped SSE bytes to write to the client now. May return an empty array when the
    /// chunk carries nothing client-visible.
    /// </summary>
    byte[] TranslateChunk(byte[] nativeChunkJson);

    /// <summary>
    /// Called once after the SDK's stream enumeration ends: flushes any buffered state and appends the
    /// terminal <c>data: [DONE]</c> line, returning the final OpenAI-shaped SSE bytes to write.
    /// </summary>
    byte[] Flush();
}

