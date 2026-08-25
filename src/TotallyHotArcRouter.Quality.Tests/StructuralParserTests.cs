using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Parsing;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers in-process structural parsing across languages, and - the distinction the scorer depends on -
/// which of those verdicts a real parser actually backs.
/// </summary>
public class StructuralParserTests
{
    private readonly StructuralParser _parser = new();

    [Fact]
    public void Check_ValidCSharp_IsAuthoritativeAndValid()
    {
        var verdict = _parser.Check("public class C { public int M() => 1; }", CodeLanguage.CSharp);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }

    [Fact]
    public void Check_InvalidCSharp_ReportsErrors()
    {
        var verdict = _parser.Check("public class C { public void M() {", CodeLanguage.CSharp);

        Assert.False(verdict.IsValid);
        Assert.True(verdict.IsAuthoritative);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public void Check_BalancedPython_IsValidButNotAuthoritative()
    {
        var verdict = _parser.Check("def f(x):\n    return [x, (x + 1)]\n", CodeLanguage.Python);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.IsAuthoritative);
    }

    [Fact]
    public void Check_UnbalancedPython_IsInvalid()
    {
        var verdict = _parser.Check("def f(x):\n    return [x, (x + 1]\n", CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public void Check_DelimiterInsideStringOrComment_IsIgnored()
    {
        var verdict = _parser.Check("x = \"a ) b\"  # trailing ] brace }\n", CodeLanguage.Python);

        Assert.True(verdict.IsValid);
    }

    [Fact]
    public void Check_EmptyHeuristic_IsInvalid()
    {
        var verdict = _parser.Check("   \n\t", CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
    }

    [Fact]
    public void Check_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Check(null!, CodeLanguage.Python));
    }

    [Fact]
    public void Check_EscapedQuoteInsideString_IsIgnored()
    {
        // The escaped quote must not end the string early - if it did, the trailing "1)" would be seen as
        // a stray close-paren and this would come back invalid.
        var verdict = _parser.Check("x = 'it\\'s (fine)' + str(1)\n", CodeLanguage.Python);

        Assert.True(verdict.IsValid);
    }

    [Fact]
    public void Check_UnbalancedCloseParen_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 1)\n", CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced ')'.", verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedCloseBracket_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 1]\n", CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced ']'.", verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedCloseBrace_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 1}\n", CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced '}'.", verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedString_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 'abc\n", CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unterminated string literal.", verdict.Errors);
    }

    [Fact]
    public void Check_UnclosedOpenDelimiter_ReportsWhichOneIsStillOpen()
    {
        var verdict = _parser.Check("x = (1 + 2\n", CodeLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced '('.", verdict.Errors);
    }

    [Fact]
    public void Check_UnknownLanguage_UsesHeuristic()
    {
        var verdict = _parser.Check("(balanced)", CodeLanguage.Unknown);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.IsAuthoritative);
    }

    // JavaScript gained an authoritative parser (Acornima) when execution was removed, because the Tier-1
    // subprocess that used to be its real syntax check went away with it.
    [Fact]
    public void Check_ValidJavaScript_IsAuthoritativeAndValid()
    {
        var verdict = _parser.Check("const add = (a, b) => a + b;" + Environment.NewLine + "export default add;", CodeLanguage.JavaScript);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }

    [Fact]
    public void Check_InvalidJavaScript_ReportsErrors()
    {
        var verdict = _parser.Check("function broken( { return 1", CodeLanguage.JavaScript);

        Assert.False(verdict.IsValid);
        Assert.True(verdict.IsAuthoritative);
        Assert.NotEmpty(verdict.Errors);
    }

    // A model's answer is as likely to be a bare statement sequence as an ES module, and the two grammars
    // disagree about top-level await and import. Accepting either is what stops good code failing on a
    // technicality.
    [Theory]
    [InlineData("var x = 1; console.log(x);")]
    [InlineData("import fs from 'fs'; export const f = () => fs;")]
    [InlineData("const data = await fetch('https://example.com');")]
    public void Check_JavaScript_AcceptsBothScriptAndModuleGrammars(string code)
    {
        Assert.True(_parser.Check(code, CodeLanguage.JavaScript).IsValid);
    }

    [Theory]
    [InlineData(CodeLanguage.Python)]
    [InlineData(CodeLanguage.Shell)]
    [InlineData(CodeLanguage.Unknown)]
    public void Check_LanguagesWithoutAParser_AreNeverReportedAuthoritative(CodeLanguage language)
    {
        Assert.False(_parser.Check("anything (balanced) here", language).IsAuthoritative);
    }

    [Theory]
    [InlineData(CodeLanguage.CSharp)]
    [InlineData(CodeLanguage.JavaScript)]
    public void Check_LanguagesWithAParser_AreAlwaysReportedAuthoritative(CodeLanguage language)
    {
        Assert.True(_parser.Check("x", language).IsAuthoritative);
        Assert.True(_parser.Check("!!! not valid in either language !!!", language).IsAuthoritative);
    }

    [Fact]
    public void Check_RejectsNullCode()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Check(null!, CodeLanguage.CSharp));
    }
}
