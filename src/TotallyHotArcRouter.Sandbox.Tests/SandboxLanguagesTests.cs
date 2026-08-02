using TotallyHot.ArcRouter.Sandbox;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers language-hint mapping and executable classification.</summary>
public class SandboxLanguagesTests
{
    [Theory]
    [InlineData("py", SandboxLanguage.Python)]
    [InlineData("python3", SandboxLanguage.Python)]
    [InlineData("js", SandboxLanguage.JavaScript)]
    [InlineData("node", SandboxLanguage.JavaScript)]
    [InlineData("ts", SandboxLanguage.JavaScript)]
    [InlineData("bash", SandboxLanguage.Shell)]
    [InlineData("cs", SandboxLanguage.CSharp)]
    [InlineData("csharp", SandboxLanguage.CSharp)]
    [InlineData("rust", SandboxLanguage.Unknown)]
    [InlineData("", SandboxLanguage.Unknown)]
    [InlineData(null, SandboxLanguage.Unknown)]
    public void FromHint_MapsKnownHints(string? hint, SandboxLanguage expected)
    {
        Assert.Equal(expected, SandboxLanguages.FromHint(hint));
    }

    [Theory]
    [InlineData(SandboxLanguage.Python, true)]
    [InlineData(SandboxLanguage.JavaScript, true)]
    [InlineData(SandboxLanguage.Shell, true)]
    [InlineData(SandboxLanguage.CSharp, false)]
    [InlineData(SandboxLanguage.Unknown, false)]
    public void IsExecutable_ClassifiesRuntimes(SandboxLanguage language, bool expected)
    {
        Assert.Equal(expected, SandboxLanguages.IsExecutable(language));
    }
}

