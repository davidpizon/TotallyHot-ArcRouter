using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Strips the JSON Schema keywords a particular downstream consumer rejects, recursively, so a schema the
/// client wrote against OpenAI's dialect can be handed to something with a narrower one.
///
/// <para>
/// Shared rather than per-translator because the recursive walk is the hard part and the keyword lists are
/// the easy part. Two consumers need it today - Gemini's OpenAPI-subset schema validator and the
/// grammar-constrained decoders behind <c>response_format</c> - and they reject overlapping but different
/// sets. Splitting the walk across both would mean two chances to forget that <c>anyOf</c>/<c>oneOf</c>
/// subschemas are validated too.
/// </para>
/// </summary>
/// <remarks>
/// Mirrors LiteLLM's <c>_remove_additional_properties</c>/<c>_remove_strict_from_schema</c>, which solve the
/// same problem for the same reason.
/// </remarks>
internal static class JsonSchemaSanitizer
{
    // Gemini's schema is an OpenAPI subset and 400s on anything outside it. `const` has no equivalent, but
    // a single-value `enum` means the same thing, so it is converted rather than dropped.
    private static readonly SanitizerProfile Gemini = new(
        RemovedKeywords: ["additionalProperties", "strict", "$schema", "$comment", "enumDescriptions"],
        ConvertConstToEnum: true);

    // Far less aggressive than Gemini's. llama.cpp's GBNF converter (and the MLX/Outlines path) handle
    // `additionalProperties`, `enum`, and `const` natively, so removing them would discard real constraints
    // and widen the grammar - the opposite of the point. Only keywords that are pure metadata to a sampler
    // (`$schema`, `$comment`, `enumDescriptions`) or that are not JSON Schema at all (`strict`, an
    // OpenAI-specific request-level flag) are stripped, since an unrecognized keyword is the common cause of
    // a hard schema-compile rejection.
    private static readonly SanitizerProfile ConstrainedDecoding = new(
        RemovedKeywords: ["strict", "$schema", "$comment", "enumDescriptions"],
        ConvertConstToEnum: false);

    /// <summary>
    /// Strips the keywords Gemini's OpenAPI-subset schema rejects and converts <c>const</c> to a
    /// single-value <c>enum</c>.
    /// </summary>
    /// <remarks>
    /// The schema is passed through <b>without</b> type-uppercasing: modern Gemini accepts lowercase
    /// JSON-Schema types, and the pinned LiteLLM reference does not uppercase them either.
    /// </remarks>
    /// <param name="schema">The schema to sanitize. Mutated in place and returned for chaining.</param>
    public static JsonObject ForGemini(JsonObject schema) => Sanitize(schema, Gemini);

    /// <summary>
    /// Strips the keywords a grammar-constrained decoder cannot compile, preserving every keyword that
    /// actually narrows the grammar.
    /// </summary>
    /// <param name="schema">The schema to sanitize. Mutated in place and returned for chaining.</param>
    public static JsonObject ForConstrainedDecoding(JsonObject schema) => Sanitize(schema, ConstrainedDecoding);

    /// <summary>
    /// Applies one profile to a schema and every subschema reachable from it.
    /// </summary>
    /// <remarks>
    /// Recurses into <c>properties</c>, <c>items</c>, and the <c>anyOf</c>/<c>oneOf</c>/<c>allOf</c>
    /// combinators. The combinators are not optional extra care: Gemini's own 400 for a violation nested
    /// under <c>anyOf</c> reports the path through <c>any_of[n]</c>, proving it validates those subschemas
    /// too rather than only the top level.
    /// </remarks>
    private static JsonObject Sanitize(JsonObject schema, SanitizerProfile profile)
    {
        foreach (var keyword in profile.RemovedKeywords)
        {
            schema.Remove(keyword);
        }

        if (profile.ConvertConstToEnum && schema["const"] is JsonNode constValue)
        {
            var clonedConst = constValue.DeepClone();
            schema.Remove("const");
            schema["enum"] = new JsonArray { clonedConst };
        }

        if (schema["properties"] is JsonObject properties)
        {
            foreach (var property in properties.ToArray())
            {
                if (property.Value is JsonObject propertySchema)
                {
                    Sanitize(propertySchema, profile);
                }
            }
        }

        if (schema["items"] is JsonObject items)
        {
            Sanitize(items, profile);
        }

        foreach (var combinator in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (schema[combinator] is JsonArray subschemas)
            {
                foreach (var subschema in subschemas)
                {
                    if (subschema is JsonObject subschemaObject)
                    {
                        Sanitize(subschemaObject, profile);
                    }
                }
            }
        }

        return schema;
    }

    /// <summary>One consumer's rules: which keywords it cannot accept, and whether it understands <c>const</c>.</summary>
    /// <param name="RemovedKeywords">Keywords deleted from every schema object encountered.</param>
    /// <param name="ConvertConstToEnum">Whether <c>const</c> must be rewritten as a single-value <c>enum</c>.</param>
    private sealed record SanitizerProfile(string[] RemovedKeywords, bool ConvertConstToEnum);
}

