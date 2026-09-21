using TotallyHot.ArcRouter.Quality.Parsing;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers the Python-specific structural scanner: the cases the shared delimiter-balance heuristic used
/// to fail on valid code (triple quotes, f-strings, prefixes) and the imbalances it must still reject.
/// </summary>
public class PythonStructuralParserTests
{
    private readonly StructuralParser _parser = new();

    // ---- Previously weak: valid Python the bracket-count heuristic rejected ----

    [Theory]
    [InlineData("s = '''It's (fine)'''\n")]
    [InlineData("s = \"\"\"She said \\\"hello\\\" (and left)\"\"\"\n")]
    [InlineData("def f():\n    '''Returns (a tuple of results).'''\n    return (1, 2)\n")]
    [InlineData("doc = \"\"\"\nunmatched ) and ] and } in a docstring\n\"\"\"\n")]
    public void Check_TripleQuotedStringWithUnmatchedPunctuation_IsValid(string code)
    {
        AssertAccepted(code);
    }

    [Theory]
    [InlineData("s = f'''It's {name}'''\n")]
    [InlineData("s = rf\"\"\"path is {p} and that's fine\"\"\"\n")]
    [InlineData("s = f\"value={d['key']}\"\n")]
    [InlineData("s = f\"Hello {person[\"name\"]}\"\n")]
    [InlineData("s = f\"item {foo(bar)} done\"\n")]
    [InlineData("s = f\"use {{literal braces}} and {x}\"\n")]
    [InlineData("s = f\"{x!r}\"\n")]
    [InlineData("s = f\"{x:.2f}\"\n")]
    [InlineData("s = f\"{x:{width}}\"\n")]
    [InlineData("s = t\"Hello {name}\"\n")]
    public void Check_InterpolatedString_IsValid(string code)
    {
        AssertAccepted(code);
    }

    [Theory]
    [InlineData("path = r\"C:\\new\\test\"\n")]
    [InlineData("path = R'C:\\foo\\bar'\n")]
    [InlineData("raw = r'''\\n (not a paren issue)'''\n")]
    [InlineData("b = b\"\\x41 (byte string)\"\n")]
    [InlineData("u = u\"hello (unicode prefix)\"\n")]
    [InlineData("br = br\"\\n (bytes-raw)\"\n")]
    public void Check_PrefixedString_IsValid(string code)
    {
        AssertAccepted(code);
    }

    [Fact]
    public void Check_EscapedQuoteInsideSingleQuotedString_IsValid()
    {
        // The same case StructuralParserTests already covers, re-stated here so a Python-only
        // regression of the escape walk cannot hide behind the generic suite.
        AssertAccepted("x = 'it\\'s (fine)' + str(1)\n");
    }

    [Fact]
    public void Check_DelimiterInsideStringOrComment_IsValid()
    {
        AssertAccepted("x = \"a ) b\"  # trailing ] brace }\n");
    }

    [Fact]
    public void Check_LineContinuationInsideString_IsValid()
    {
        AssertAccepted("x = \"hello \\\nworld (still in the string)\"\n");
    }

    [Fact]
    public void Check_FStringExpressionWithNestedParensAndComment_IsValid()
    {
        AssertAccepted("s = f\"\"\"{\n    foo(bar)  # uses )\n}\"\"\"\n");
    }

    [Fact]
    public void Check_FStringInequalityIsNotAConversionSuffix_IsValid()
    {
        // `!` starts a conversion only when followed by s/r/a; `x!=1` is a comparison.
        AssertAccepted("s = f\"{x!=1}\"\n");
    }

    [Fact]
    public void Check_TypicalFunction_IsValidButNotAuthoritative()
    {
        var verdict = _parser.Check(code: "def f(x):\n    return [x, (x + 1)]\n", language: CodeLanguage.Python);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }

    // ---- Still invalid: real imbalances the scanner must not start swallowing ----

    [Fact]
    public void Check_UnbalancedCloseParen_IsInvalid()
    {
        var verdict = _parser.Check(code: "x = 1)\n", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced ')'.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedOpenParen_IsInvalid()
    {
        var verdict = _parser.Check(code: "x = (1 + 2\n", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced '('.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedSingleQuotedString_IsInvalid()
    {
        var verdict = _parser.Check(code: "x = 'abc\n", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unterminated string literal.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedTripleQuotedString_IsInvalid()
    {
        var verdict = _parser.Check(code: "s = '''still going\n", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unterminated string literal.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedFStringExpression_IsInvalid()
    {
        var verdict = _parser.Check(code: "s = f\"{foo(bar\"\n", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public void Check_MismatchedBracketsInsideCode_IsInvalid()
    {
        var verdict = _parser.Check(code: "def f(x):\n    return [x, (x + 1]\n", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public void Check_EmptySnippet_IsInvalid()
    {
        var verdict = _parser.Check(code: "   \n\t", language: CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Snippet is empty.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PythonStructuralParser.Check(code: null!));
    }

    /// <summary>
    /// Pins the gap the new scanner closed: the generic delimiter walk still rejects a triple-quoted
    /// apostrophe, which is valid Python and must not fail the quality grade.
    /// </summary>
    [Fact]
    public void DelimiterBalance_StillRejectsTheTripleQuotedApostropheCase()
    {
        const string code = "s = '''It's (fine)'''\n";

        Assert.False(DelimiterBalance.IsBalanced(code: code, error: out _));
        AssertAccepted(code);
    }

    private void AssertAccepted(string code)
    {
        var verdict = _parser.Check(code: code, language: CodeLanguage.Python);

        Assert.True(verdict.IsValid, userMessage: string.Join("; ", values: verdict.Errors));
        Assert.False(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }
}
