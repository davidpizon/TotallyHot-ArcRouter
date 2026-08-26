using AwesomeAssertions;
using TotallyHot.ArcRouter.Updater;

namespace TotallyHot.ArcRouter.Updater.Tests;

public class ArgumentParserTests
{
    /// <summary>A well-formed 64-character lowercase-hex SHA256, used wherever a test needs a valid <c>--expected-sha256</c>.</summary>
    internal const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Parse_AllArgumentsPresent_ReturnsParsedArguments()
    {
        var args = new[]
        {
            "--install-dir", @"C:\Program Files\TotallyHotArcRouter\Router",
            "--zip-path", @"C:\temp\update.zip",
            "--service-name", "TotallyHotArcRouter",
            "--wait-pid", "12345",
            "--expected-sha256", Sha,
        };

        var result = ArgumentParser.Parse(args);

        result.InstallDirectory.Should().Be(@"C:\Program Files\TotallyHotArcRouter\Router");
        result.ZipPath.Should().Be(@"C:\temp\update.zip");
        result.ServiceName.Should().Be("TotallyHotArcRouter");
        result.WaitPid.Should().Be(12345);
        result.ExpectedSha256.Should().Be(Sha);
    }

    [Fact]
    public void Parse_OrderIndependent_StillParsesCorrectly()
    {
        var args = new[]
        {
            "--wait-pid", "99",
            "--expected-sha256", Sha,
            "--service-name", "Svc",
            "--zip-path", "zip.zip",
            "--install-dir", "dir",
        };

        var result = ArgumentParser.Parse(args);

        result.Should().Be(new UpdaterArguments("dir", "zip.zip", "Svc", 99, Sha));
    }

    [Fact]
    public void Parse_UppercaseSha256_IsAcceptedAndNormalizedToLowercase()
    {
        var args = new[]
        {
            "--install-dir", "d", "--zip-path", "z", "--service-name", "s", "--wait-pid", "1",
            "--expected-sha256", Sha.ToUpperInvariant(),
        };

        var result = ArgumentParser.Parse(args);

        result.ExpectedSha256.Should().Be(Sha);
    }

    [Fact]
    public void Parse_MissingExpectedSha256_Throws()
    {
        var args = new[] { "--install-dir", "d", "--zip-path", "z", "--service-name", "s", "--wait-pid", "1" };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--expected-sha256*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void Parse_MalformedExpectedSha256_Throws(string value)
    {
        var args = new[] { "--install-dir", "d", "--zip-path", "z", "--service-name", "s", "--wait-pid", "1", "--expected-sha256", value };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--expected-sha256*");
    }

    [Fact]
    public void Parse_MissingInstallDir_Throws()
    {
        var args = new[] { "--zip-path", "z", "--service-name", "s", "--wait-pid", "1", "--expected-sha256", ArgumentParserTests.Sha };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--install-dir*");
    }

    [Fact]
    public void Parse_MissingZipPath_Throws()
    {
        var args = new[] { "--install-dir", "d", "--service-name", "s", "--wait-pid", "1", "--expected-sha256", ArgumentParserTests.Sha };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--zip-path*");
    }

    [Fact]
    public void Parse_MissingServiceName_Throws()
    {
        var args = new[] { "--install-dir", "d", "--zip-path", "z", "--wait-pid", "1", "--expected-sha256", ArgumentParserTests.Sha };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--service-name*");
    }

    [Fact]
    public void Parse_MissingWaitPid_Throws()
    {
        var args = new[] { "--install-dir", "d", "--zip-path", "z", "--service-name", "s", "--expected-sha256", ArgumentParserTests.Sha };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--wait-pid*");
    }

    [Fact]
    public void Parse_WaitPidNotAnInteger_Throws()
    {
        var args = new[] { "--install-dir", "d", "--zip-path", "z", "--service-name", "s", "--wait-pid", "not-a-number", "--expected-sha256", ArgumentParserTests.Sha };

        var act = () => ArgumentParser.Parse(args);

        act.Should().Throw<ArgumentException>().WithMessage("*--wait-pid*");
    }

    [Fact]
    public void Parse_EmptyArgs_ThrowsWithAllFiveErrors()
    {
        var act = () => ArgumentParser.Parse([]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*--install-dir*--zip-path*--service-name*--wait-pid*--expected-sha256*");
    }

    [Fact]
    public void Parse_NullArgs_Throws()
    {
        var act = () => ArgumentParser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExtractOption_FlagAtEndWithNoValue_ReturnsNull()
    {
        var result = ArgumentParser.ExtractOption(["--install-dir"], "--install-dir");

        result.Should().BeNull();
    }
}
