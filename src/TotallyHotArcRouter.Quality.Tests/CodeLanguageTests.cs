namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers language-hint mapping and which languages a real parser backs.</summary>
public class CodeLanguagesTests
{
    [Theory]
    [InlineData("py", CodeLanguage.Python)]
    [InlineData("python3", CodeLanguage.Python)]
    [InlineData("js", CodeLanguage.JavaScript)]
    [InlineData("node", CodeLanguage.JavaScript)]
    [InlineData("ts", CodeLanguage.JavaScript)]
    [InlineData("bash", CodeLanguage.Shell)]
    [InlineData("cs", CodeLanguage.CSharp)]
    [InlineData("csharp", CodeLanguage.CSharp)]
    [InlineData("rust", CodeLanguage.Unknown)]
    [InlineData("", CodeLanguage.Unknown)]
    [InlineData(null, CodeLanguage.Unknown)]
    public void FromHint_MapsKnownHints(string? hint, CodeLanguage expected)
    {
        Assert.Equal(expected: expected, actual: CodeLanguages.FromHint(hint));
    }

    [Theory]
    [InlineData(CodeLanguage.CSharp, true)]
    [InlineData(CodeLanguage.JavaScript, true)]
    [InlineData(CodeLanguage.Python, false)]
    [InlineData(CodeLanguage.Shell, false)]
    [InlineData(CodeLanguage.Unknown, false)]
    public void HasAuthoritativeParser_ReportsWhichLanguagesAreReallyParsed(CodeLanguage language, bool expected)
    {
        Assert.Equal(expected: expected, actual: CodeLanguages.HasAuthoritativeParser(language));
    }
}