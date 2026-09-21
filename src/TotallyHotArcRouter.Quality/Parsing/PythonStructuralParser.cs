namespace TotallyHot.ArcRouter.Quality.Parsing;

/// <summary>
/// Non-authoritative in-process structural check for Python snippets. Walks the text the way a lexer
/// would - prefixes, triple quotes, f/t-string interpolations, comments, and bracketing delimiters -
/// and reports the first imbalance it can be sure of.
/// </summary>
/// <remarks>
/// This is not a Python compiler. No managed parser exists for Python that this assembly can take
/// without also taking an interpreter (IronPython was rejected for that reason), so the verdict stays
/// marked non-authoritative and the scorer weighs it at half. The point of being language-aware rather
/// than counting brackets is to stop common real outputs - a docstring with an apostrophe, an f-string
/// with nested quotes, a raw path - from failing the older delimiter-balance heuristic.
/// </remarks>
internal static class PythonStructuralParser
{
    /// <summary>
    /// Checks <paramref name="code"/> as Python. Empty snippets are invalid; everything else is valid
    /// unless a string, interpolation, or bracketing delimiter is left open or mismatched.
    /// </summary>
    /// <param name="code">The Python snippet to check.</param>
    /// <returns>A non-authoritative <see cref="SyntaxVerdict"/>.</returns>
    public static SyntaxVerdict Check(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (string.IsNullOrWhiteSpace(code))
            return SyntaxVerdict.Invalid(language: CodeLanguage.Python, false, errors: ["Snippet is empty."]);

        return new Scan(code).TryRun(out var error)
            ? SyntaxVerdict.Valid(language: CodeLanguage.Python, false)
            : SyntaxVerdict.Invalid(language: CodeLanguage.Python, false, errors: [error!]);
    }

    /// <summary>
    /// Single left-to-right pass over a Python snippet. Instance state is the scan cursor; the only
    /// public entry is <see cref="PythonStructuralParser.Check"/>.
    /// </summary>
    private sealed class Scan
    {
        private readonly string _code;
        private readonly Stack<char> _delimiters = new();
        private int _i;
        private string? _error;

        /// <summary>Initializes a scan over <paramref name="code"/>.</summary>
        /// <param name="code">The snippet to walk.</param>
        public Scan(string code)
        {
            _code = code;
        }

        /// <summary>Walks the snippet and reports the first structural error, if any.</summary>
        /// <param name="error">On failure, a short description of the first imbalance found.</param>
        /// <returns><see langword="true"/> when the snippet is structurally balanced.</returns>
        public bool TryRun(out string? error)
        {
            while (_i < _code.Length && _error is null)
            {
                var c = _code[_i];

                if (c == '#')
                {
                    SkipLineComment();
                    continue;
                }

                if (TryConsumeString()) continue;

                switch (c)
                {
                    case '(':
                    case '[':
                    case '{':
                        _delimiters.Push(c);
                        _i++;
                        break;
                    case ')':
                        Close(open: '(', close: ')');
                        break;
                    case ']':
                        Close(open: '[', close: ']');
                        break;
                    case '}':
                        Close(open: '{', close: '}');
                        break;
                    default:
                        _i++;
                        break;
                }
            }

            if (_error is not null)
            {
                error = _error;
                return false;
            }

            if (_delimiters.Count > 0)
            {
                error = FormattableString.Invariant($"Unbalanced '{_delimiters.Peek()}'.");
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Consumes a string at the cursor if one starts here, including a leading <c>r</c>/<c>f</c>/<c>b</c>/<c>u</c>/<c>t</c>
        /// prefix and a triple-quoted opener.
        /// </summary>
        /// <returns><see langword="true"/> when a string was consumed (or rejected as unterminated).</returns>
        private bool TryConsumeString()
        {
            var prefixEnd = _i;
            while (prefixEnd < _code.Length && IsPrefixChar(_code[prefixEnd])) prefixEnd++;

            if (prefixEnd >= _code.Length) return false;

            var quote = _code[prefixEnd];
            if (quote is not '"' and not '\'') return false;

            var interpolating = false;
            for (var k = _i; k < prefixEnd; k++)
            {
                if (_code[k] is 'f' or 'F' or 't' or 'T') interpolating = true;
            }

            var triple = prefixEnd + 2 < _code.Length && _code[prefixEnd + 1] == quote && _code[prefixEnd + 2] == quote;

            _i = prefixEnd;
            ConsumeString(quote: quote, triple: triple, interpolating: interpolating);
            return true;
        }

        /// <summary>
        /// Consumes one string literal starting at the opening quote currently under the cursor.
        /// Interpolation (f/t-strings) recursively scans <c>{...}</c> expressions so a nested quote does
        /// not close the outer string - the failure that made PEP 701 f-strings look unbalanced.
        /// </summary>
        /// <param name="quote">The quote character that opened the string.</param>
        /// <param name="triple">Whether this is a triple-quoted literal.</param>
        /// <param name="interpolating">Whether <c>{...}</c> expressions are live inside the string.</param>
        private void ConsumeString(char quote, bool triple, bool interpolating)
        {
            _i++;
            if (triple) _i += 2;

            while (_i < _code.Length)
            {
                var c = _code[_i];

                if (c == '\\')
                {
                    SkipEscape();
                    continue;
                }

                if (interpolating && c == '{')
                {
                    if (Peek(1) == '{')
                    {
                        _i += 2;
                        continue;
                    }

                    _i++;
                    ConsumeInterpolatedExpression();
                    if (_error is not null) return;

                    continue;
                }

                if (interpolating && c == '}')
                {
                    if (Peek(1) == '}')
                    {
                        _i += 2;
                        continue;
                    }

                    Fail("Unbalanced '}'.");
                    return;
                }

                if (c == quote)
                {
                    if (triple)
                    {
                        if (Peek(1) == quote && Peek(2) == quote)
                        {
                            _i += 3;
                            return;
                        }

                        _i++;
                        continue;
                    }

                    _i++;
                    return;
                }

                if (!triple && c is '\n' or '\r')
                {
                    Fail("Unterminated string literal.");
                    return;
                }

                _i++;
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>
        /// Scans the expression inside an f/t-string <c>{...}</c>, including nested strings, conversion
        /// suffixes (<c>!s</c>/<c>!r</c>/<c>!a</c>), and a format spec after <c>:</c>.
        /// </summary>
        private void ConsumeInterpolatedExpression()
        {
            var local = new Stack<char>();

            while (_i < _code.Length && _error is null)
            {
                var c = _code[_i];

                if (c == '#')
                {
                    SkipLineComment();
                    continue;
                }

                if (TryConsumeString()) continue;

                if (local.Count == 0)
                {
                    if (c == '!' && Peek(1) is 's' or 'r' or 'a' or 'S' or 'R' or 'A')
                    {
                        var after = Peek(2);
                        if (after is '}' or ':' or '=' or '\0')
                        {
                            _i += 2;
                            continue;
                        }
                    }

                    if (c == ':')
                    {
                        _i++;
                        ConsumeFormatSpec();
                        return;
                    }

                    if (c == '}')
                    {
                        _i++;
                        return;
                    }
                }

                switch (c)
                {
                    case '(':
                    case '[':
                    case '{':
                        local.Push(c);
                        _i++;
                        break;
                    case ')':
                        CloseLocal(local: local, open: '(', close: ')');
                        break;
                    case ']':
                        CloseLocal(local: local, open: '[', close: ']');
                        break;
                    case '}':
                        CloseLocal(local: local, open: '{', close: '}');
                        break;
                    default:
                        _i++;
                        break;
                }
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>
        /// Scans an f-string format spec until the field's closing <c>}</c>, allowing nested interpolations.
        /// </summary>
        private void ConsumeFormatSpec()
        {
            while (_i < _code.Length && _error is null)
            {
                var c = _code[_i];

                if (c == '{')
                {
                    if (Peek(1) == '{')
                    {
                        _i += 2;
                        continue;
                    }

                    _i++;
                    ConsumeInterpolatedExpression();
                    continue;
                }

                if (c == '}')
                {
                    _i++;
                    return;
                }

                if (c == '\\')
                {
                    SkipEscape();
                    continue;
                }

                _i++;
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>Skips a <c>#</c> comment through the end of the line, leaving the newline unconsumed.</summary>
        private void SkipLineComment()
        {
            while (_i < _code.Length && _code[_i] is not '\n' and not '\r') _i++;
        }

        /// <summary>
        /// Skips a backslash and the character it escapes, treating backslash-newline as one line continuation.
        /// </summary>
        private void SkipEscape()
        {
            _i++;
            if (_i >= _code.Length) return;

            if (_code[_i] == '\r')
            {
                _i++;
                if (_i < _code.Length && _code[_i] == '\n') _i++;

                return;
            }

            _i++;
        }

        /// <summary>Pops <paramref name="open"/> from the module-level delimiter stack, or fails on mismatch.</summary>
        /// <param name="open">The opener this closer is expected to match.</param>
        /// <param name="close">The closer that was just seen, used in the error text.</param>
        private void Close(char open, char close)
        {
            if (_delimiters.Count == 0 || _delimiters.Peek() != open)
            {
                Fail(FormattableString.Invariant($"Unbalanced '{close}'."));
                return;
            }

            _delimiters.Pop();
            _i++;
        }

        /// <summary>Pops <paramref name="open"/> from an interpolation-local stack, or fails on mismatch.</summary>
        /// <param name="local">The interpolation expression's own delimiter stack.</param>
        /// <param name="open">The opener this closer is expected to match.</param>
        /// <param name="close">The closer that was just seen, used in the error text.</param>
        private void CloseLocal(Stack<char> local, char open, char close)
        {
            if (local.Count == 0 || local.Peek() != open)
            {
                Fail(FormattableString.Invariant($"Unbalanced '{close}'."));
                return;
            }

            local.Pop();
            _i++;
        }

        /// <summary>Records the first error; subsequent failures are ignored so the original cause is kept.</summary>
        /// <param name="message">The diagnostic to report.</param>
        private void Fail(string message) => _error ??= message;

        /// <summary>Returns the character <paramref name="ahead"/> steps from the cursor, or NUL at end of input.</summary>
        /// <param name="ahead">How many characters ahead to look.</param>
        /// <returns>The character at that offset, or <c>\0</c> past the end.</returns>
        private char Peek(int ahead)
        {
            var j = _i + ahead;
            return j < _code.Length ? _code[j] : '\0';
        }

        /// <summary>Indicates whether <paramref name="c"/> can appear in a Python string prefix (<c>r</c>/<c>u</c>/<c>f</c>/<c>b</c>/<c>t</c>).</summary>
        /// <param name="c">The character to test.</param>
        /// <returns><see langword="true"/> for a prefix letter in either case.</returns>
        private static bool IsPrefixChar(char c) => c is 'r' or 'R' or 'u' or 'U' or 'f' or 'F' or 'b' or 'B' or 't' or 'T';
    }
}
