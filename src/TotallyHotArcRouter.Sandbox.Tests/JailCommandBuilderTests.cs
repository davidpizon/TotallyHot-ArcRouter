using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Tier1;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the jail command/namespace-flag builder.</summary>
public class JailCommandBuilderTests
{
    [Theory]
    [InlineData(SandboxLanguage.Python, "python3", "snippet.py")]
    [InlineData(SandboxLanguage.JavaScript, "node", "snippet.js")]
    [InlineData(SandboxLanguage.Shell, "sh", "snippet.sh")]
    public void Build_ReturnsInterpreterAndScript(SandboxLanguage language, string interpreter, string script)
    {
        var command = JailCommandBuilder.Build(language);

        Assert.Equal(interpreter, command.Interpreter);
        Assert.Equal(script, command.ScriptFileName);
    }

    [Fact]
    public void Build_IncludesNetworkAndPidNamespaces()
    {
        var command = JailCommandBuilder.Build(SandboxLanguage.Python);

        Assert.Contains("--net", command.UnshareFlags);
        Assert.Contains("--pid", command.UnshareFlags);
        Assert.Contains("--mount", command.UnshareFlags);
    }

    [Theory]
    [InlineData(SandboxLanguage.CSharp)]
    [InlineData(SandboxLanguage.Unknown)]
    public void Build_UnsupportedLanguage_Throws(SandboxLanguage language)
    {
        Assert.Throws<NotSupportedException>(() => JailCommandBuilder.Build(language));
    }

    [Theory]
    [InlineData(SandboxLanguage.Python, true)]
    [InlineData(SandboxLanguage.Shell, true)]
    [InlineData(SandboxLanguage.CSharp, false)]
    public void IsSupported_ClassifiesLanguages(SandboxLanguage language, bool expected)
    {
        Assert.Equal(expected, JailCommandBuilder.IsSupported(language));
    }
}

