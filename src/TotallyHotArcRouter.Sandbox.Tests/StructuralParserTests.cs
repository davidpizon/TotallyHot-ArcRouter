using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Parsing;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers Tier-0 structural parsing across languages.</summary>
public class StructuralParserTests
{
    private readonly StructuralParser _parser = new();

    [Fact]
    public void Check_ValidCSharp_IsAuthoritativeAndValid()
    {
        var verdict = _parser.Check("public class C { public int M() => 1; }", SandboxLanguage.CSharp);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }

    [Fact]
    public void Check_InvalidCSharp_ReportsErrors()
    {
        var verdict = _parser.Check("public class C { public void M() {", SandboxLanguage.CSharp);

        Assert.False(verdict.IsValid);
        Assert.True(verdict.IsAuthoritative);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public void Check_BalancedPython_IsValidButNotAuthoritative()
    {
        var verdict = _parser.Check("def f(x):\n    return [x, (x + 1)]\n", SandboxLanguage.Python);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.IsAuthoritative);
    }

    [Fact]
    public void Check_UnbalancedPython_IsInvalid()
    {
        var verdict = _parser.Check("def f(x):\n    return [x, (x + 1]\n", SandboxLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public void Check_DelimiterInsideStringOrComment_IsIgnored()
    {
        var verdict = _parser.Check("x = \"a ) b\"  # trailing ] brace }\n", SandboxLanguage.Python);

        Assert.True(verdict.IsValid);
    }

    [Fact]
    public void Check_EmptyHeuristic_IsInvalid()
    {
        var verdict = _parser.Check("   \n\t", SandboxLanguage.Shell);

        Assert.False(verdict.IsValid);
    }

    [Fact]
    public void Check_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Check(null!, SandboxLanguage.Python));
    }

    [Fact]
    public void Check_EscapedQuoteInsideString_IsIgnored()
    {
        // The escaped quote must not end the string early - if it did, the trailing "1)" would be seen as
        // a stray close-paren and this would come back invalid.
        var verdict = _parser.Check("x = 'it\\'s (fine)' + str(1)\n", SandboxLanguage.Python);

        Assert.True(verdict.IsValid);
    }

    [Fact]
    public void Check_UnbalancedCloseParen_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 1)\n", SandboxLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced ')'.", verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedCloseBracket_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 1]\n", SandboxLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced ']'.", verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedCloseBrace_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 1}\n", SandboxLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced '}'.", verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedString_ReportsSpecificError()
    {
        var verdict = _parser.Check("x = 'abc\n", SandboxLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unterminated string literal.", verdict.Errors);
    }

    [Fact]
    public void Check_UnclosedOpenDelimiter_ReportsWhichOneIsStillOpen()
    {
        var verdict = _parser.Check("x = (1 + 2\n", SandboxLanguage.Python);

        Assert.False(verdict.IsValid);
        Assert.Contains("Unbalanced '('.", verdict.Errors);
    }

    [Fact]
    public void Check_UnknownLanguage_UsesHeuristic()
    {
        var verdict = _parser.Check("(balanced)", SandboxLanguage.Unknown);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.IsAuthoritative);
    }
}

