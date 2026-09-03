namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Finds the JSON objects embedded in a model's free text, with no knowledge of tags, name keys, or
/// argument keys - the one piece of tool-call scanning that is genuinely dialect-independent.
/// </summary>
/// <remarks>
/// Extracted unchanged from the original <c>ToolCallEchoScanner</c> of
/// <c>docs/router/unified-api-translation.md</c> §4.5, which is where its brace-balancing was written and
/// proven against a live LM Studio repro. Phase 4 deleted that scanner's dialect-specific half - the
/// hardcoded <c>name</c>+<c>arguments</c> shape check - in favor of <see cref="DialectMatcher"/>, but this
/// half was already correct for every dialect and is reused verbatim rather than rewritten.
/// </remarks>
internal static class JsonObjectScanner
{
    /// <summary>
    /// Finds every top-level (not nested inside another object) balanced-brace JSON object substring in
    /// <paramref name="text"/>, respecting quoted strings and escape sequences so a literal <c>{</c>/<c>}</c>
    /// inside a string value never miscounts brace depth.
    /// </summary>
    /// <param name="text">The raw text to scan.</param>
    public static IEnumerable<string> FindTopLevelJsonObjects(string text)
    {
        var matches = new List<string>();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            var start = i;
            var depth = 0;
            var inString = false;
            var escapeNext = false;
            var j = i;
            var closed = false;

            for (; j < text.Length; j++)
            {
                var c = text[j];

                if (inString)
                {
                    if (escapeNext)
                        escapeNext = false;
                    else if (c == '\\')
                        escapeNext = true;
                    else if (c == '"') inString = false;

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        matches.Add(text[start..(j + 1)]);
                        closed = true;
                        break;
                    }
                }
            }

            // An unterminated object (ran off the end of the text without closing) contributes nothing, but
            // resume right after its opening brace rather than abandoning the scan - a stray/unbalanced `{`
            // earlier in the text must not hide a later, well-formed tool-call object.
            i = closed ? j + 1 : start + 1;
        }

        return matches;
    }
}

/// <summary>One tool call extracted from a dialect-framed region of a model's reply.</summary>
/// <param name="Name">The function name.</param>
/// <param name="ArgumentsJson">
/// The arguments, as a serialized JSON string (matching OpenAI's <c>tool_calls</c> delta
/// shape).
/// </param>
internal sealed record ExtractedToolCall(string Name, string ArgumentsJson);