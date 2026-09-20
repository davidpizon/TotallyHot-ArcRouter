using System.Text;

namespace TotallyHot.ArcRouter.Quality.Parsing;

/// <summary>
/// Non-authoritative in-process structural check for POSIX/bash snippets. Walks the text the way a
/// shell lexer would - quoting, <c>$(...)</c> / <c>${...}</c> / backticks, here-documents, comments, and
/// <c>case</c>/<c>esac</c> pattern <c>)</c> - and reports the first imbalance it can be sure of.
/// </summary>
/// <remarks>
/// This is not a shell grammar. There is no managed POSIX parser this assembly can take, so the
/// verdict stays marked non-authoritative and the scorer weighs it at half. The point of being
/// language-aware rather than counting brackets is to stop common real outputs - a here-document whose
/// body contains a stray <c>)</c>, <c>${#var}</c> / <c>${var#pattern}</c>, a <c>case</c> arm - from
/// failing the older delimiter-balance heuristic.
/// </remarks>
internal static class ShellStructuralParser
{
    /// <summary>
    /// Checks <paramref name="code"/> as shell. Empty snippets are invalid; everything else is valid
    /// unless a quote, here-document, or bracketing delimiter is left open or mismatched.
    /// </summary>
    /// <param name="code">The shell snippet to check.</param>
    /// <returns>A non-authoritative <see cref="SyntaxVerdict"/>.</returns>
    public static SyntaxVerdict Check(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (string.IsNullOrWhiteSpace(code))
            return SyntaxVerdict.Invalid(language: CodeLanguage.Shell, false, errors: ["Snippet is empty."]);

        return new Scan(code).TryRun(out var error)
            ? SyntaxVerdict.Valid(language: CodeLanguage.Shell, false)
            : SyntaxVerdict.Invalid(language: CodeLanguage.Shell, false, errors: [error!]);
    }

    /// <summary>
    /// Single left-to-right pass over a shell snippet. Instance state is the scan cursor; the only
    /// public entry is <see cref="ShellStructuralParser.Check"/>.
    /// </summary>
    private sealed class Scan
    {
        private readonly string _code;
        private readonly Stack<char> _delimiters = new();
        private readonly Stack<int> _caseOpenDepths = new();
        private readonly Queue<HereDoc> _hereDocs = new();
        private int _i;
        private string? _error;

        /// <summary>A here-document queued on the current command line, consumed after the next unescaped newline.</summary>
        /// <param name="Delimiter">The terminator line that ends the body.</param>
        /// <param name="StripLeadingTabs">Whether <c>&lt;&lt;-</c> stripping of leading tabs applies.</param>
        private readonly record struct HereDoc(string Delimiter, bool StripLeadingTabs);

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
            while (_i < _code.Length && _error is null) Step();

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

            if (_hereDocs.Count > 0)
            {
                error = "Unterminated here-document.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Advances until input is exhausted, an error is recorded, or the delimiter stack returns to
        /// <paramref name="stopDepth"/> - the last of which is how <c>$(...)</c> and <c>$((...))</c> close.
        /// </summary>
        /// <param name="stopDepth">The delimiter-stack depth at which this region is done.</param>
        private void ScanRegion(int stopDepth)
        {
            while (_i < _code.Length && _error is null && _delimiters.Count > stopDepth) Step();
        }

        /// <summary>Consumes one token, operator, or character of unquoted shell command text.</summary>
        private void Step()
        {
            var c = _code[_i];

            if (c is '\n' or '\r')
            {
                ConsumeNewline();
                FlushHereDocs();
                return;
            }

            if (c == '\\')
            {
                SkipEscapeOrContinuation();
                return;
            }

            if (c == '#' && CanStartComment())
            {
                SkipLineComment();
                return;
            }

            if (c == '\'')
            {
                ConsumeSingleQuoted();
                return;
            }

            if (c == '"')
            {
                ConsumeDoubleQuoted();
                return;
            }

            if (c == '`')
            {
                ConsumeBacktick();
                return;
            }

            if (c == '$')
            {
                ConsumeDollar();
                return;
            }

            if (c == '<' && Peek(1) == '<' && Peek(2) != '<')
            {
                QueueHereDoc();
                return;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                ConsumeWord();
                return;
            }

            switch (c)
            {
                case '(':
                case '[':
                case '{':
                    _delimiters.Push(c);
                    _i++;
                    break;
                case ')':
                    CloseParen();
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

        /// <summary>
        /// Closes a <c>)</c>. A <c>case</c> pattern terminator is a <c>)</c> that does not match an
        /// opener pushed after that <c>case</c> - treating it as a closer is what made every <c>case</c>
        /// arm look unbalanced under the delimiter-balance heuristic.
        /// </summary>
        private void CloseParen()
        {
            if (_caseOpenDepths.Count > 0 && _delimiters.Count == _caseOpenDepths.Peek())
            {
                _i++;
                return;
            }

            Close(open: '(', close: ')');
        }

        /// <summary>Pops <paramref name="open"/> from the delimiter stack, or fails on mismatch.</summary>
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

        /// <summary>
        /// Consumes a <c>$</c> expansion: <c>$var</c>, <c>$#</c>, <c>${...}</c>, <c>$(...)</c>,
        /// <c>$((...))</c>, <c>$'...'</c>, or <c>$"..."</c>.
        /// </summary>
        private void ConsumeDollar()
        {
            _i++;
            if (_i >= _code.Length) return;

            switch (_code[_i])
            {
                case '\'':
                    ConsumeAnsiCQuoted();
                    return;
                case '"':
                    ConsumeDoubleQuoted();
                    return;
                case '{':
                    ConsumeParamExpansion();
                    return;
                case '(':
                    ConsumeParenSubstitution();
                    return;
            }
        }

        /// <summary>
        /// Consumes <c>$(...)</c> or <c>$((...))</c> by pushing the opener(s) and scanning a nested
        /// command/arithmetic region until they close.
        /// </summary>
        private void ConsumeParenSubstitution()
        {
            var arithmetic = Peek(1) == '(';
            var stopDepth = _delimiters.Count;
            _delimiters.Push('(');
            _i++;
            if (arithmetic)
            {
                _delimiters.Push('(');
                _i++;
            }

            ScanRegion(stopDepth: stopDepth);
        }

        /// <summary>
        /// Consumes <c>${...}</c>, including nested expansions, without treating <c>#</c> as a comment.
        /// That is what keeps <c>${#var}</c> and <c>${var#pattern}</c> from looking like an unclosed brace.
        /// </summary>
        private void ConsumeParamExpansion()
        {
            _i++;
            var depth = 1;

            while (_i < _code.Length && _error is null)
            {
                var c = _code[_i];

                if (c == '\'')
                {
                    ConsumeSingleQuoted();
                    continue;
                }

                if (c == '"')
                {
                    ConsumeDoubleQuoted();
                    continue;
                }

                if (c == '`')
                {
                    ConsumeBacktick();
                    continue;
                }

                if (c == '$')
                {
                    ConsumeDollar();
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    _i++;
                    continue;
                }

                if (c == '}')
                {
                    _i++;
                    depth--;
                    if (depth == 0) return;

                    continue;
                }

                _i++;
            }

            Fail("Unbalanced '{'.");
        }

        /// <summary>Consumes a single-quoted string; backslash is literal, newlines are allowed.</summary>
        private void ConsumeSingleQuoted()
        {
            _i++;
            while (_i < _code.Length)
            {
                if (_code[_i] == '\'')
                {
                    _i++;
                    return;
                }

                _i++;
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>
        /// Consumes a <c>$'...'</c> ANSI-C quoted string, where backslash escapes (including <c>\'</c>)
        /// are processed. Treating it as a POSIX single-quoted string would end the quote at the escaped
        /// apostrophe and then fail the rest of a perfectly ordinary <c>$'it\'s'</c>.
        /// </summary>
        private void ConsumeAnsiCQuoted()
        {
            _i++;
            while (_i < _code.Length)
            {
                var c = _code[_i];
                if (c == '\'')
                {
                    _i++;
                    return;
                }

                if (c == '\\')
                {
                    _i += 2;
                    continue;
                }

                _i++;
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>
        /// Consumes a double-quoted string, still expanding <c>$(...)</c>, <c>${...}</c>, and backticks
        /// so nested quoting in command substitutions does not end the string early.
        /// </summary>
        private void ConsumeDoubleQuoted()
        {
            _i++;
            while (_i < _code.Length && _error is null)
            {
                var c = _code[_i];

                if (c == '"')
                {
                    _i++;
                    return;
                }

                if (c == '\\')
                {
                    _i += 2;
                    continue;
                }

                if (c == '`')
                {
                    ConsumeBacktick();
                    continue;
                }

                if (c == '$')
                {
                    ConsumeDollar();
                    continue;
                }

                _i++;
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>Consumes a backtick command substitution, expanding nested <c>$</c> forms.</summary>
        private void ConsumeBacktick()
        {
            _i++;
            while (_i < _code.Length && _error is null)
            {
                var c = _code[_i];

                if (c == '`')
                {
                    _i++;
                    return;
                }

                if (c == '\\')
                {
                    _i += 2;
                    continue;
                }

                if (c == '$')
                {
                    ConsumeDollar();
                    continue;
                }

                _i++;
            }

            Fail("Unterminated string literal.");
        }

        /// <summary>
        /// Consumes a shell word and tracks <c>case</c>/<c>esac</c>. <c>case=1</c> is an assignment, not
        /// the keyword, so it does not open a pattern-matching region.
        /// </summary>
        private void ConsumeWord()
        {
            var start = _i;
            while (_i < _code.Length && IsWordChar(_code[_i])) _i++;

            var word = _code.AsSpan(start, _i - start);
            if (word.Equals("case", StringComparison.Ordinal) && Peek(0) != '=')
            {
                _caseOpenDepths.Push(_delimiters.Count);
                return;
            }

            if (word.Equals("esac", StringComparison.Ordinal) && _caseOpenDepths.Count > 0)
                _caseOpenDepths.Pop();
        }

        /// <summary>
        /// Queues a here-document opened by <c>&lt;&lt;</c> or <c>&lt;&lt;-</c>. The body is consumed at
        /// the next unescaped newline rather than on this line, matching how the shell defers it.
        /// </summary>
        private void QueueHereDoc()
        {
            _i += 2;
            var stripLeadingTabs = false;
            if (_i < _code.Length && _code[_i] == '-')
            {
                stripLeadingTabs = true;
                _i++;
            }

            SkipSpacesAndTabs();
            var delimiter = ReadHereDocDelimiter();
            if (delimiter.Length == 0) return;

            _hereDocs.Enqueue(new HereDoc(Delimiter: delimiter, StripLeadingTabs: stripLeadingTabs));
        }

        /// <summary>Reads the here-document terminator, stripping optional quotes around it.</summary>
        /// <returns>The delimiter token, or empty when <c>&lt;&lt;</c> was not followed by a word.</returns>
        private string ReadHereDocDelimiter()
        {
            if (_i >= _code.Length) return string.Empty;

            var c = _code[_i];
            if (c is '\'' or '"')
            {
                var quote = c;
                _i++;
                var buffer = new StringBuilder();
                while (_i < _code.Length && _code[_i] != quote && _code[_i] is not '\n' and not '\r')
                {
                    buffer.Append(_code[_i]);
                    _i++;
                }

                if (_i < _code.Length && _code[_i] == quote) _i++;

                return buffer.ToString();
            }

            if (c == '\\' && _i + 1 < _code.Length)
            {
                _i++;
                return ReadBareHereDocDelimiter();
            }

            return ReadBareHereDocDelimiter();
        }

        /// <summary>Reads an unquoted here-document delimiter word.</summary>
        /// <returns>The delimiter, or empty at end of input / a newline.</returns>
        private string ReadBareHereDocDelimiter()
        {
            var start = _i;
            while (_i < _code.Length && !IsHereDocDelimiterEnd(_code[_i])) _i++;

            return _code[start.._i];
        }

        /// <summary>Consumes every queued here-document body in the order the operators appeared.</summary>
        private void FlushHereDocs()
        {
            while (_hereDocs.Count > 0 && _error is null)
            {
                var spec = _hereDocs.Dequeue();
                ConsumeHereDocBody(spec);
            }
        }

        /// <summary>
        /// Consumes lines until one equals the here-document delimiter. Body text is never scanned for
        /// delimiters, which is what keeps a stray <c>)</c> in the body from failing the snippet.
        /// </summary>
        /// <param name="spec">The queued here-document to consume.</param>
        private void ConsumeHereDocBody(HereDoc spec)
        {
            while (_i < _code.Length)
            {
                var line = ReadLine();
                var candidate = spec.StripLeadingTabs ? line.TrimStart('\t') : line;
                if (candidate.Equals(spec.Delimiter, StringComparison.Ordinal)) return;
            }

            Fail("Unterminated here-document.");
        }

        /// <summary>Reads one line without the terminator, advancing past <c>\r</c>/<c>\n</c>.</summary>
        /// <returns>The line contents, not including the newline.</returns>
        private string ReadLine()
        {
            var start = _i;
            while (_i < _code.Length && _code[_i] is not '\n' and not '\r') _i++;

            var line = _code[start.._i];
            ConsumeNewline();
            return line;
        }

        /// <summary>Advances past a <c>\n</c>, <c>\r\n</c>, or lone <c>\r</c>.</summary>
        private void ConsumeNewline()
        {
            if (_i >= _code.Length) return;

            if (_code[_i] == '\r') _i++;

            if (_i < _code.Length && _code[_i] == '\n') _i++;
        }

        /// <summary>Skips a <c>#</c> comment through the end of the line, leaving the newline unconsumed.</summary>
        private void SkipLineComment()
        {
            while (_i < _code.Length && _code[_i] is not '\n' and not '\r') _i++;
        }

        /// <summary>
        /// Skips a backslash. A backslash-newline is a line continuation (and does not start here-document
        /// bodies); any other escaped character is consumed as a literal so <c>\(</c> does not open a group.
        /// </summary>
        private void SkipEscapeOrContinuation()
        {
            _i++;
            if (_i >= _code.Length) return;

            if (_code[_i] is '\n' or '\r')
            {
                ConsumeNewline();
                return;
            }

            _i++;
        }

        /// <summary>Skips spaces and tabs but not newlines.</summary>
        private void SkipSpacesAndTabs()
        {
            while (_i < _code.Length && _code[_i] is ' ' or '\t') _i++;
        }

        /// <summary>
        /// Indicates whether <c>#</c> at the cursor starts a comment. It does not when it continues a
        /// word or a parameter operator, so <c>${#var}</c>, <c>$#</c>, and <c>foo#bar</c> stay intact.
        /// </summary>
        /// <returns><see langword="true"/> when a comment starts here.</returns>
        private bool CanStartComment()
        {
            if (_i == 0) return true;

            var prev = _code[_i - 1];
            if (char.IsAsciiLetterOrDigit(prev)) return false;

            return prev is not '_' and not '$' and not '{' and not '}' and not '#' and not '%'
                and not '/' and not ':' and not '-' and not '!' and not '*';
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

        /// <summary>Indicates whether <paramref name="c"/> is a POSIX shell name character.</summary>
        /// <param name="c">The character to test.</param>
        /// <returns><see langword="true"/> for ASCII letters, digits, and underscore.</returns>
        private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

        /// <summary>Indicates whether <paramref name="c"/> ends an unquoted here-document delimiter.</summary>
        /// <param name="c">The character to test.</param>
        /// <returns><see langword="true"/> for whitespace or a shell operator that cannot be in the word.</returns>
        private static bool IsHereDocDelimiterEnd(char c) =>
            char.IsWhiteSpace(c) || c is ';' or '&' or '|' or '<' or '>' or '(' or ')' or '{';
    }
}
