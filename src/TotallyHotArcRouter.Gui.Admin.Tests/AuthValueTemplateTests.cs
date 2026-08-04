namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Covers <see cref="AuthValueTemplate"/>, the one place the provider editor's single "Value" box is
/// translated into the scheme + environment-variable pair the proxy actually stores. Everything about the
/// new credential UI resting on a lossless round-trip through this type, these tests assert both
/// directions and, just as importantly, that an unrepresentable template is refused rather than silently
/// truncated into a provider that fails to authenticate for no visible reason.
/// </summary>
public sealed class AuthValueTemplateTests
{
    [Fact]
    public void TryParse_BareName_YieldsNoSchemeAndTheName()
    {
        Assert.True(AuthValueTemplate.TryParse("ANTHROPIC_API_KEY", out var scheme, out var name, out var error));

        Assert.Empty(scheme);
        Assert.Equal("ANTHROPIC_API_KEY", name);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_BracedNameWithNoPrefix_YieldsNoScheme()
    {
        Assert.True(AuthValueTemplate.TryParse("{env:AZURE_OPENAI_API_KEY}", out var scheme, out var name, out _));

        Assert.Empty(scheme);
        Assert.Equal("AZURE_OPENAI_API_KEY", name);
    }

    [Fact]
    public void TryParse_BearerTemplate_SplitsSchemeFromName()
    {
        Assert.True(AuthValueTemplate.TryParse("Bearer {env:OPENAI_API_KEY}", out var scheme, out var name, out _));

        Assert.Equal("Bearer", scheme);
        Assert.Equal("OPENAI_API_KEY", name);
    }

    [Fact]
    public void TryParse_TrimsSurroundingWhitespace()
    {
        Assert.True(AuthValueTemplate.TryParse("  Bearer  {env: OPENAI_API_KEY }  ", out var scheme, out var name, out _));

        Assert.Equal("Bearer", scheme);
        Assert.Equal("OPENAI_API_KEY", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_Blank_Fails(string? template)
    {
        Assert.False(AuthValueTemplate.TryParse(template, out _, out _, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_BarePrefixWithoutBraces_FailsRatherThanStoringAnUnresolvableName()
    {
        // "Bearer OPENAI_API_KEY" almost certainly means the braced form. Storing it as a variable name
        // would look right in the editor and never resolve at request time.
        Assert.False(AuthValueTemplate.TryParse("Bearer OPENAI_API_KEY", out _, out _, out var error));

        Assert.Contains("{env:VAR_NAME}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_MultipleReferences_Fails()
    {
        Assert.False(AuthValueTemplate.TryParse("{env:A}{env:B}", out _, out _, out var error));

        Assert.Contains("Only one", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_TextAfterTheReference_Fails()
    {
        // The storage shape is scheme + key joined with a space, so there is nowhere to put a suffix.
        Assert.False(AuthValueTemplate.TryParse("Bearer {env:KEY}-suffix", out _, out _, out var error));

        Assert.Contains("must come last", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_UnclosedReference_Fails()
    {
        Assert.False(AuthValueTemplate.TryParse("Bearer {env:KEY", out _, out _, out var error));

        Assert.Contains("Unclosed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_EmptyReference_Fails()
    {
        Assert.False(AuthValueTemplate.TryParse("Bearer {env:}", out _, out _, out var error));

        Assert.Contains("missing a variable name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_MultiWordPrefix_Fails()
    {
        // AuthHeaderScheme is a single token joined with one space; a multi-word prefix cannot round-trip.
        Assert.False(AuthValueTemplate.TryParse("Token of {env:KEY}", out _, out _, out var error));

        Assert.Contains("single word", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_NoScheme_ReturnsTheBareName()
    {
        Assert.Equal("ANTHROPIC_API_KEY", AuthValueTemplate.Compose(string.Empty, "ANTHROPIC_API_KEY"));
        Assert.Equal("ANTHROPIC_API_KEY", AuthValueTemplate.Compose(null, "ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Compose_WithScheme_ReturnsTheBracedTemplate()
    {
        Assert.Equal("Bearer {env:OPENAI_API_KEY}", AuthValueTemplate.Compose("Bearer", "OPENAI_API_KEY"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Compose_NoVariable_ReturnsEmptySoThePlaceholderShows(string? envVarName)
    {
        // Returning "Bearer" alone would look like a half-entered credential rather than an empty field.
        Assert.Empty(AuthValueTemplate.Compose("Bearer", envVarName));
    }

    [Theory]
    [InlineData("", "ANTHROPIC_API_KEY")]
    [InlineData("Bearer", "OPENAI_API_KEY")]
    [InlineData("Token", "COHERE_API_KEY")]
    public void ComposeThenParse_RoundTripsTheStoredPair(string scheme, string envVarName)
    {
        var composed = AuthValueTemplate.Compose(scheme, envVarName);

        Assert.True(AuthValueTemplate.TryParse(composed, out var parsedScheme, out var parsedName, out _));
        Assert.Equal(scheme, parsedScheme);
        Assert.Equal(envVarName, parsedName);
    }
}
