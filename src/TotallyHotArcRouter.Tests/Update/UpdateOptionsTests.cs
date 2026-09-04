using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

public sealed class UpdateOptionsTests
{
    [Fact]
    public void EnsureValid_Defaults_DoesNotThrow()
    {
        var options = new UpdateOptions();

        var ex = Record.Exception(options.EnsureValid);

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureValid_RelativeApiBaseUrl_Throws()
    {
        var options = new UpdateOptions { GitHubApiBaseUrl = "not-a-url" };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_ZeroPollInterval_Throws()
    {
        var options = new UpdateOptions { PollInterval = TimeSpan.Zero };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_NegativePollInterval_Throws()
    {
        var options = new UpdateOptions { PollInterval = TimeSpan.FromSeconds(-1) };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }
}