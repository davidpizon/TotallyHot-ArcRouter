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
        Assert.True(AuthValueTemplate.TryParse(template: "ANTHROPIC_API_KEY", scheme: out var scheme,
            envVarName: out var name, error: out var error));

        Assert.Empty(scheme);
        Assert.Equal(expected: "ANTHROPIC_API_KEY", actual: name);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_BracedNameWithNoPrefix_YieldsNoScheme()
    {
        Assert.True(AuthValueTemplate.TryParse(template: "{env:AZURE_OPENAI_API_KEY}", scheme: out var scheme,
            envVarName: out var name, error: out _));

        Assert.Empty(scheme);
        Assert.Equal(expected: "AZURE_OPENAI_API_KEY", actual: name);
    }

    [Fact]
    public void TryParse_BearerTemplate_SplitsSchemeFromName()
    {
        Assert.True(AuthValueTemplate.TryParse(template: "Bearer {env:OPENAI_API_KEY}", scheme: out var scheme,
            envVarName: out var name, error: out _));

        Assert.Equal(expected: "Bearer", actual: scheme);
        Assert.Equal(expected: "OPENAI_API_KEY", actual: name);
    }

    [Fact]
    public void TryParse_TrimsSurroundingWhitespace()
    {
        Assert.True(AuthValueTemplate.TryParse(template: "  Bearer  {env: OPENAI_API_KEY }  ", scheme: out var scheme,
            envVarName: out var name, error: out _));

        Assert.Equal(expected: "Bearer", actual: scheme);
        Assert.Equal(expected: "OPENAI_API_KEY", actual: name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_Blank_Fails(string? template)
    {
        Assert.False(AuthValueTemplate.TryParse(template: template, scheme: out _, envVarName: out _,
            error: out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_BarePrefixWithoutBraces_FailsRatherThanStoringAnUnresolvableName()
    {
        // "Bearer OPENAI_API_KEY" almost certainly means the braced form. Storing it as a variable name
        // would look right in the editor and never resolve at request time.
        Assert.False(AuthValueTemplate.TryParse(template: "Bearer OPENAI_API_KEY", scheme: out _, envVarName: out _,
            error: out var error));

        Assert.Contains(expectedSubstring: "{env:VAR_NAME}", actualString: error,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_MultipleReferences_Fails()
    {
        Assert.False(AuthValueTemplate.TryParse(template: "{env:A}{env:B}", scheme: out _, envVarName: out _,
            error: out var error));

        Assert.Contains(expectedSubstring: "Only one", actualString: error, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_TextAfterTheReference_Fails()
    {
        // The storage shape is scheme + key joined with a space, so there is nowhere to put a suffix.
        Assert.False(AuthValueTemplate.TryParse(template: "Bearer {env:KEY}-suffix", scheme: out _, envVarName: out _,
            error: out var error));

        Assert.Contains(expectedSubstring: "must come last", actualString: error,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_UnclosedReference_Fails()
    {
        Assert.False(AuthValueTemplate.TryParse(template: "Bearer {env:KEY", scheme: out _, envVarName: out _,
            error: out var error));

        Assert.Contains(expectedSubstring: "Unclosed", actualString: error, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_EmptyReference_Fails()
    {
        Assert.False(AuthValueTemplate.TryParse(template: "Bearer {env:}", scheme: out _, envVarName: out _,
            error: out var error));

        Assert.Contains(expectedSubstring: "missing a variable name", actualString: error,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_MultiWordPrefix_Fails()
    {
        // AuthHeaderScheme is a single token joined with one space; a multi-word prefix cannot round-trip.
        Assert.False(AuthValueTemplate.TryParse(template: "Token of {env:KEY}", scheme: out _, envVarName: out _,
            error: out var error));

        Assert.Contains(expectedSubstring: "single word", actualString: error,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_NoScheme_ReturnsTheBareName()
    {
        Assert.Equal(expected: "ANTHROPIC_API_KEY",
            actual: AuthValueTemplate.Compose(scheme: string.Empty, envVarName: "ANTHROPIC_API_KEY"));
        Assert.Equal(expected: "ANTHROPIC_API_KEY",
            actual: AuthValueTemplate.Compose(null, envVarName: "ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Compose_WithScheme_ReturnsTheBracedTemplate()
    {
        Assert.Equal(expected: "Bearer {env:OPENAI_API_KEY}",
            actual: AuthValueTemplate.Compose(scheme: "Bearer", envVarName: "OPENAI_API_KEY"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Compose_NoVariable_ReturnsEmptySoThePlaceholderShows(string? envVarName)
    {
        // Returning "Bearer" alone would look like a half-entered credential rather than an empty field.
        Assert.Empty(AuthValueTemplate.Compose(scheme: "Bearer", envVarName: envVarName));
    }

    [Theory]
    [InlineData("", "ANTHROPIC_API_KEY")]
    [InlineData("Bearer", "OPENAI_API_KEY")]
    [InlineData("Token", "COHERE_API_KEY")]
    public void ComposeThenParse_RoundTripsTheStoredPair(string scheme, string envVarName)
    {
        var composed = AuthValueTemplate.Compose(scheme: scheme, envVarName: envVarName);

        Assert.True(AuthValueTemplate.TryParse(template: composed, scheme: out var parsedScheme,
            envVarName: out var parsedName, error: out _));
        Assert.Equal(expected: scheme, actual: parsedScheme);
        Assert.Equal(expected: envVarName, actual: parsedName);
    }
}