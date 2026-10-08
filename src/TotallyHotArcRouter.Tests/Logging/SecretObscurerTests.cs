using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Tests.Logging;

/// <summary>Pins <see cref="SecretObscurer"/>'s key-shaped replacements for #184 phase 3 body excerpts.</summary>
public sealed class SecretObscurerTests
{
    /// <summary>An OpenAI-style key is replaced so it never reaches a body log file.</summary>
    [Fact]
    public void Obscure_ReplacesSkStyleApiKey()
    {
        var obscured = SecretObscurer.Obscure("Authorization: Bearer sk-abcdefghijklmnopqrstuvwxyz012345");

        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz012345", obscured, StringComparison.Ordinal);
        Assert.Contains(SecretObscurer.RedactedToken, obscured, StringComparison.Ordinal);
    }

    /// <summary>Every starting shape named in the #165 plan is replaced, including AWS key IDs and PEM blocks.</summary>
    [Theory]
    [InlineData("key AKIAIOSFODNN7EXAMPLE end", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuv end", "sk-ant-api03-abcdefghijklmnopqrstuv")]
    [InlineData("tok ghp_abcdefghijklmnopqrstuvwxyz0123 end", "ghp_abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("tok github_pat_11ABCDEFG0abcdefghijkl_mnop end", "github_pat_11ABCDEFG0abcdefghijkl_mnop")]
    [InlineData("tok xoxb-1234567890-abcdefghij end", "xoxb-1234567890-abcdefghij")]
    [InlineData("tok xoxp-1234567890-abcdefghij end", "xoxp-1234567890-abcdefghij")]
    [InlineData("pem -----BEGIN RSA PRIVATE KEY-----\nMIIabc\ndef\n-----END RSA PRIVATE KEY----- end", "MIIabc")]
    [InlineData("pem -----BEGIN PRIVATE KEY-----\nMIIabc", "MIIabc")]
    public void Obscure_ReplacesEachPlanShape(string text, string secret)
    {
        var obscured = SecretObscurer.Obscure(text);

        Assert.DoesNotContain(secret, obscured, StringComparison.Ordinal);
        Assert.Contains(SecretObscurer.RedactedToken, obscured, StringComparison.Ordinal);
    }

    /// <summary>Ordinary prose without key-shaped spans is left alone.</summary>
    [Fact]
    public void Obscure_LeavesOrdinaryTextAlone()
    {
        const string text = "The application's name is Totally Hot Arc Router.";
        Assert.Equal(text, SecretObscurer.Obscure(text));
    }

    /// <summary>Null and empty inputs stay empty rather than throwing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Obscure_NullOrEmpty_ReturnsEmpty(string? text)
    {
        Assert.Equal(string.Empty, SecretObscurer.Obscure(text));
    }
}
