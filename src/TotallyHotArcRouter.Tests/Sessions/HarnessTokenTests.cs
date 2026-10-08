using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>Pins the harness allowlist: a known product keeps name and version, anything else is only a length.</summary>
public sealed class HarnessTokenTests
{
    /// <summary>Allowlisted products keep their name and version and drop everything else in the header.</summary>
    [Theory]
    [InlineData("claude-cli/2.1.286 (external, cli)", "claude-cli/2.1.286")]
    [InlineData("codex_cli_rs/0.46.0 (Windows 10.0.26100; x86_64) WindowsTerminal", "codex_cli_rs/0.46.0")]
    [InlineData("Claude-CLI/2.0.0-beta+7", "claude-cli/2.0.0-beta+7")]
    [InlineData("claude-code", "claude-code")]
    [InlineData("claude-cli/ weird", "claude-cli")]
    public void Normalize_KnownProduct_KeepsNameAndVersionOnly(string userAgent, string expected) =>
        Assert.Equal(expected, HarnessToken.Normalize(userAgent));

    /// <summary>Any other product is recorded as <c>other</c> and the header's length, never the header.</summary>
    [Theory]
    [InlineData("curl/8.5.0", "other:10")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) david-laptop", "other:44")]
    [InlineData("claude-cli-evil/1.0", "other:19")]
    [InlineData("", "other:0")]
    [InlineData(null, "other:0")]
    public void Normalize_UnknownProduct_ReportsOnlyLength(string? userAgent, string expected) =>
        Assert.Equal(expected, HarnessToken.Normalize(userAgent));

    /// <summary>A hostname or secret riding in a known product's comment never reaches the token.</summary>
    [Fact]
    public void Normalize_CommentText_IsNeverCopied()
    {
        var token = HarnessToken.Normalize("claude-cli/2.1.286 (host=david-laptop; key=sk-abcdefghijklmnopqrstuv)");

        Assert.Equal("claude-cli/2.1.286", token);
    }
}
