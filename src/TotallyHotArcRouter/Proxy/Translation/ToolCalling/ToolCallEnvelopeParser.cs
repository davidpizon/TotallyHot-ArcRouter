using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Reads the <c>{"content", "tool_calls"}</c> envelope that constrained decoding produces back into prose
/// plus a list of calls - the response counterpart to <see cref="ToolCallEnvelopeSchema"/>.
/// <para>
/// Shared by the buffered and streaming translators for the same reason <see cref="DialectMatcher"/> is:
/// two implementations of "what counts as a call" drift, and the streaming path is the one where a
/// disagreement is hardest to notice. Strict parsing throughout - the whole point of constraining the
/// sampler is that the reply either <em>is</em> the envelope or something has gone wrong upstream, so
/// there is no salvage heuristic here and deliberately so.
/// </para>
/// </summary>
internal static class ToolCallEnvelopeParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses one complete envelope.
    /// </summary>
    /// <param name="envelopeJson">The model's whole <c>content</c> string, which under constrained decoding is the envelope.</param>
    /// <param name="prose">The envelope's <c>content</c> field, or empty when absent.</param>
    /// <param name="calls">The envelope's <c>tool_calls</c>, normalized to OpenAI's serialized-arguments shape.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="envelopeJson"/> is not a JSON object - the caller must
    /// then fail open and forward the text as ordinary content rather than dropping it.
    /// </returns>
    public static bool TryParse(string envelopeJson, out string prose, out IReadOnlyList<ExtractedToolCall> calls)
    {
        prose = string.Empty;
        calls = [];

        JsonObject envelope;
        try
        {
            if (JsonNode.Parse(envelopeJson) is not JsonObject parsed) return false;

            envelope = parsed;
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope[ToolCallEnvelopeSchema.ContentProperty] is JsonValue contentValue &&
            contentValue.TryGetValue<string>(out var text))
            prose = text;

        if (envelope[ToolCallEnvelopeSchema.ToolCallsProperty] is not JsonArray toolCalls)
            // A schema-constrained reply always carries the key, but a server that ignored the schema (or an
            // operator override pointing this dialect at a model whose endpoint silently dropped it) may
            // not. Prose with no calls is a complete, valid answer, so this is a success, not a failure.
            return true;

        var extracted = new List<ExtractedToolCall>();
        foreach (var node in toolCalls)
        {
            if (node is not JsonObject call ||
                call["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) ||
                string.IsNullOrEmpty(name))
                continue;

            // OpenAI carries arguments as a serialized JSON *string*, while the envelope carries them as a
            // real nested object - so this re-serializes rather than reading a string out. An absent
            // arguments object becomes "{}" rather than being skipped: the grammar requires the key, so its
            // absence means the server ignored the schema, and a call with no arguments is still a call.
            var arguments = call["arguments"];
            extracted.Add(new ExtractedToolCall(
                Name: name,
                ArgumentsJson: arguments?.ToJsonString(SerializerOptions) ?? "{}"));
        }

        calls = extracted;
        return true;
    }
}