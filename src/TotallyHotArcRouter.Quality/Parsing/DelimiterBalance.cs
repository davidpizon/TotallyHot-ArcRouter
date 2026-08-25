namespace TotallyHot.ArcRouter.Quality.Parsing;

/// <summary>
/// A cheap, in-process structural sanity check: verifies bracketing delimiters are balanced, skipping
/// content inside single/double/back-quoted strings and <c>#</c> line comments. Not a real parser — a
/// best-effort Tier-0 signal for languages the host cannot parse in-process. The authoritative check for
/// those languages is a Tier-1 subprocess (py_compile / node --check / sh -n).
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
        char quote = '\0';
        var inLineComment = false;

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++; // skip the escaped character
                    continue;
                }

                if (c == quote)
                {
                    quote = '\0';
                }

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
                    if (!TryClose(stack, '('))
                    {
                        error = "Unbalanced ')'.";
                        return false;
                    }

                    break;
                case ']':
                    if (!TryClose(stack, '['))
                    {
                        error = "Unbalanced ']'.";
                        return false;
                    }

                    break;
                case '}':
                    if (!TryClose(stack, '{'))
                    {
                        error = "Unbalanced '}'.";
                        return false;
                    }

                    break;
                default:
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

    /// <summary>Pops the delimiter stack if it is non-empty and the top entry matches the expected opening character, indicating a balanced close.</summary>
    private static bool TryClose(Stack<char> stack, char expectedOpen) =>
        stack.Count > 0 && stack.Pop() == expectedOpen;
}

