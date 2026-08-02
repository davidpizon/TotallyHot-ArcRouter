using TotallyHot.ArcRouter.Sandbox.Redaction;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers stdout/stderr secret redaction.</summary>
public class OutputRedactorTests
{
    [Theory]
    [InlineData("token=sk-abcdef012345678901234567890", "sk-")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Bearer abc")]
    [InlineData("aws key AKIAIOSFODNN7EXAMPLE here", "AKIA")]
    [InlineData("api_key: supersecretvalue", "supersecretvalue")]
    public void Redact_RemovesSecrets(string input, string secretFragment)
    {
        var result = OutputRedactor.Redact(input);

        Assert.DoesNotContain(secretFragment, result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesPemPrivateKeyBlock()
    {
        const string input = "before -----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY----- after";

        var result = OutputRedactor.Redact(input);

        Assert.DoesNotContain("MIIabc", result, StringComparison.Ordinal);
        Assert.Contains("before", result, StringComparison.Ordinal);
        Assert.Contains("after", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_LeavesOrdinaryOutputUntouched()
    {
        const string input = "Hello, world!\nResult: 42\n";

        Assert.Equal(input, OutputRedactor.Redact(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, OutputRedactor.Redact(input));
    }
}

