using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// The result of scanning one complete assistant message for tool calls in a given dialect.
/// </summary>
/// <param name="Dialect">
/// The dialect the scan ran under - carried so <see cref="DialectMatcher.MatchAny"/>'s caller learns
/// *which* dialect matched, which is what Phase 4 persists as the model's detected capability.
/// </param>
/// <param name="ToolCalls">
/// Every well-shaped call found, in the order they appear. Empty when the dialect's delimiters
/// never matched, or matched but framed nothing parseable.
/// </param>
/// <param name="ResidualContent">
/// Everything outside a successfully-rewritten region, concatenated in order - what the client should
/// still receive as ordinary assistant content. A region that failed to parse contributes its **raw
/// text, delimiters included**, which is what makes the scan fail open.
/// </param>
internal sealed record DialectMatchResult(
    ToolCallDialect Dialect,
    IReadOnlyList<ExtractedToolCall> ToolCalls,
    string ResidualContent);

/// <summary>
/// Finds dialect-framed tool calls in a complete assistant message (see
/// <c>docs/router/tool-call-normalization.md</c> §3.1).
/// <para>
/// This operates on a **whole message**, deliberately. The hard part of the shipping echo guard is
/// that a slow local model streams one token per SSE event, so a delimiter can be split across
/// arbitrarily many chunks - but that cross-chunk accumulation is SSE framing state, not dialect
/// logic. Keeping this layer pure and message-shaped means every dialect can be exhaustively tested
/// with no streaming harness, and Phase 4's stateful stream translator has one job (deciding when a
/// complete region has arrived) rather than two.
/// </para>
/// <para>
/// Fail-open is the invariant, matching <c>unified-api-translation.md</c> §4.5: anything that does not
/// parse into a real call is forwarded as ordinary text, never dropped and never thrown on. A
/// heuristic rewrite that guesses wrong must degrade to "the user sees the model's raw output", which
/// is recoverable, rather than to a truncated or failed response, which is not.
/// </para>
/// </summary>
internal static class DialectMatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Scans <paramref name="content"/> for calls framed in <paramref name="dialect"/>.
    /// A non-scannable dialect (<see cref="ToolCallDialectRegistry.OpenAiNative"/>) returns the content
    /// untouched with no calls, so callers need no special case for the native path.
    /// </summary>
    /// <param name="content">One complete assistant message's text.</param>
    /// <param name="dialect">The dialect to scan under.</param>
    public static DialectMatchResult ExtractToolCalls(string content, ToolCallDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(dialect);

        if (!dialect.IsScannable || content.Length == 0)
            return new DialectMatchResult(Dialect: dialect, ToolCalls: [], ResidualContent: content);

        var calls = new List<ExtractedToolCall>();
        var residual = new StringBuilder();
        var position = 0;

        while (position < content.Length)
        {
            if (!TryFindNextOpener(content: content, startIndex: position, dialect: dialect,
                    openIndex: out var openIndex, delimiter: out var delimiter))
            {
                residual.Append(value: content, startIndex: position, count: content.Length - position);
                break;
            }

            residual.Append(value: content, startIndex: position, count: openIndex - position);

            var bodyStart = openIndex + delimiter.Open.Length;
            int bodyEnd;
            int regionEnd;

            if (delimiter.Close is null)
            {
                // No closing token: the payload owns the rest of the message (Mistral's [TOOL_CALLS]).
                bodyEnd = content.Length;
                regionEnd = content.Length;
            }
            else
            {
                var closeIndex = content.IndexOf(value: delimiter.Close, startIndex: bodyStart,
                    comparisonType: StringComparison.Ordinal);
                if (closeIndex < 0)
                {
                    // An unterminated region - the model was cut off mid-call, or the "delimiter" was
                    // really prose. Forward everything from the opener onward verbatim and stop: there
                    // is no trustworthy place to resume scanning inside text we could not frame.
                    residual.Append(value: content, startIndex: openIndex, count: content.Length - openIndex);
                    break;
                }

                bodyEnd = closeIndex;
                regionEnd = closeIndex + delimiter.Close.Length;
            }

            var extracted = ExtractFromRegionBody(body: content[bodyStart..bodyEnd], dialect: dialect);
            if (extracted.Count == 0)
                // Framed, but not a call - most often the *schema* documentation echoed back (which has
                // no top-level name/arguments), or malformed JSON. Fail open with the raw region,
                // delimiters included, exactly as the shipping guard does.
                residual.Append(value: content, startIndex: openIndex, count: regionEnd - openIndex);
            else
                calls.AddRange(extracted);

            position = regionEnd;
        }

        return new DialectMatchResult(Dialect: dialect, ToolCalls: calls, ResidualContent: residual.ToString());
    }

    /// <summary>
    /// Scans <paramref name="content"/> against several dialects and returns the first that yields at
    /// least one well-shaped call, or <see langword="null"/> when none do.
    /// <para>
    /// This is the union scan Phase 4 arms for a model whose dialect is not yet known: the first live
    /// request carrying <c>tools</c> is forwarded natively and its response classified here, so
    /// detection costs no extra inference and the request still works. Candidate order decides an
    /// ambiguous attribution - see <see cref="ToolCallDialectRegistry.ScannableDialects"/>.
    /// </para>
    /// </summary>
    /// <param name="content">One complete assistant message's text.</param>
    /// <param name="candidates">The dialects to try, in tie-break order.</param>
    public static DialectMatchResult? MatchAny(string content, IReadOnlyList<ToolCallDialect> candidates)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates)
        {
            var result = ExtractToolCalls(content: content, dialect: candidate);
            if (result.ToolCalls.Count > 0) return result;
        }

        return null;
    }

    /// <summary>
    /// Finds the earliest opening delimiter at or after <paramref name="startIndex"/>. On a tie at the
    /// same index the longer token wins, so a more specific delimiter can never be shadowed by a
    /// shorter one that happens to be its prefix.
    /// </summary>
    private static bool TryFindNextOpener(
        string content,
        int startIndex,
        ToolCallDialect dialect,
        out int openIndex,
        out DialectDelimiter delimiter)
    {
        openIndex = -1;
        delimiter = dialect.Delimiters[0];

        foreach (var candidate in dialect.Delimiters)
        {
            var index = content.IndexOf(value: candidate.Open, startIndex: startIndex,
                comparisonType: StringComparison.Ordinal);
            if (index < 0) continue;

            if (openIndex < 0 || index < openIndex ||
                (index == openIndex && candidate.Open.Length > delimiter.Open.Length))
            {
                openIndex = index;
                delimiter = candidate;
            }
        }

        return openIndex >= 0;
    }

    /// <summary>
    /// Pulls every well-shaped call out of one framed region's body: a JSON object carrying a non-empty
    /// string under the dialect's name key plus any value under its arguments key.
    /// <para>
    /// Reuses <see cref="JsonObjectScanner.FindTopLevelJsonObjects"/> rather than reimplementing it -
    /// that scan's brace balancing correctly ignores braces inside quoted strings and escape sequences,
    /// and is entirely dialect-independent. It also means a JSON *array* payload needs no special
    /// handling: <c>[{a},{b}]</c> yields both objects exactly as two sequential objects would.
    /// </para>
    /// <para>
    /// Internal rather than private so <see cref="ToolCallNormalizingStreamTranslator"/> can call it
    /// directly. That translator owns delimiter framing itself, because a delimiter can be split across
    /// arbitrarily many SSE events - but once it has a complete region body, the shape check must be the
    /// identical one the buffered path applies, or the two would disagree about what counts as a call.
    /// </para>
    /// </summary>
    /// <param name="body">The raw text between a matched pair of delimiters (or after a close-less opener).</param>
    /// <param name="dialect">The dialect whose name/arguments keys the body is checked against.</param>
    internal static List<ExtractedToolCall> ExtractFromRegionBody(string body, ToolCallDialect dialect)
    {
        var results = new List<ExtractedToolCall>();

        foreach (var candidate in JsonObjectScanner.FindTopLevelJsonObjects(body))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject obj) continue;

            if (obj[dialect.NameKey] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) ||
                string.IsNullOrEmpty(name))
                continue;

            if (obj[dialect.ArgumentsKey] is not { } argumentsNode) continue;

            // OpenAI's tool_calls delta always carries function.arguments as an already-serialized JSON
            // string, never a nested object - so an object-shaped payload is serialized here, while a
            // model that already emitted a string is passed through as-is rather than double-encoded.
            var argumentsJson = argumentsNode is JsonValue argValue && argValue.TryGetValue<string>(out var raw)
                ? raw
                : argumentsNode.ToJsonString(SerializerOptions);

            results.Add(new ExtractedToolCall(Name: name, ArgumentsJson: argumentsJson));
        }

        return results;
    }
}