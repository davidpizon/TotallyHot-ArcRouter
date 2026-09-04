namespace TotallyHot.ArcRouter.Quality.Parsing;

/// <summary>
/// A cheap, in-process structural sanity check: verifies bracketing delimiters are balanced, skipping
/// content inside single/double/back-quoted strings and <c>#</c> line comments. Not a real parser — a
/// best-effort, non-authoritative signal for languages (Python, shell) that this assembly has no managed
/// parser for. There is no further, more-authoritative check for those languages: this heuristic is the
/// only one <see cref="StructuralParser"/> runs for them, and it reports itself non-authoritative via
/// <see cref="SyntaxVerdict.IsAuthoritative"/> rather than a subprocess ever being spawned to confirm it.
/// </summary>
internal static class DelimiterBalance
{
    /// <summary>Determines whether the bracketing delimiters in <paramref name="code"/> are balanced.</summary>
    /// <param name="code">The source code to check.</param>
    /// <param name="error">On failure, a short description of the first imbalance found.</param>
    /// <returns><see langword="true"/> when balanced; otherwise <see langword="false"/>.</returns>
    public static bool IsBalanced(string code, out string? error)
    {
        var stack = new Stack<char>();
        var quote = '\0';
        var inLineComment = false;

        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;

                continue;
            }

            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++; // skip the escaped character
                    continue;
                }

                if (c == quote) quote = '\0';

                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                case '`':
                    quote = c;
                    break;
                case '#':
                    inLineComment = true;
                    break;
                case '(':
                case '[':
                case '{':
                    stack.Push(c);
                    break;
                case ')':
                    if (!TryClose(stack: stack, '('))
                    {
                        error = "Unbalanced ')'.";
                        return false;
                    }

                    break;
                case ']':
                    if (!TryClose(stack: stack, '['))
                    {
                        error = "Unbalanced ']'.";
                        return false;
                    }

                    break;
                case '}':
                    if (!TryClose(stack: stack, '{'))
                    {
                        error = "Unbalanced '}'.";
                        return false;
                    }

                    break;
            }
        }

        if (quote != '\0')
        {
            error = "Unterminated string literal.";
            return false;
        }

        if (stack.Count > 0)
        {
            error = FormattableString.Invariant($"Unbalanced '{stack.Peek()}'.");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Pops the delimiter stack if it is non-empty and the top entry matches the expected opening character,
    /// indicating a balanced close.
    /// </summary>
    private static bool TryClose(Stack<char> stack, char expectedOpen)
    {
        return stack.Count > 0 && stack.Pop() == expectedOpen;
    }
}