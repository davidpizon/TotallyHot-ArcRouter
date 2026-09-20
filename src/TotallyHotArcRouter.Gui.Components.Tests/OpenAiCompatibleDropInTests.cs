using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the GUI <see cref="OpenAiCompatibleDropIn"/> constants used by
/// <see cref="ClientDropInPanel"/>. The router-side class owns the same values; a lockstep
/// test there greps this source file so the two copies cannot drift.
/// </summary>
public sealed class OpenAiCompatibleDropInTests
{
    [Fact]
    public void Default_loopback_install_is_one_base_url_and_model_auto()
    {
        Assert.Equal(expected: "https://localhost:47101/v1", actual: OpenAiCompatibleDropIn.BaseUrl);
        Assert.Equal(expected: "https://localhost:47104", actual: OpenAiCompatibleDropIn.DashboardUrl);
        Assert.Equal(expected: "auto", actual: OpenAiCompatibleDropIn.Model);
        Assert.Equal(expected: "not-needed", actual: OpenAiCompatibleDropIn.PlaceholderApiKey);
    }

    [Fact]
    public void Environment_exports_are_copy_paste_unix_lines()
    {
        var env = OpenAiCompatibleDropIn.BuildEnvironmentExports();

        Assert.Equal(
            expected:
            $"export OPENAI_BASE_URL={OpenAiCompatibleDropIn.BaseUrl}\nexport OPENAI_API_KEY={OpenAiCompatibleDropIn.PlaceholderApiKey}",
            actual: env);
        Assert.DoesNotContain("\r", env);
    }
}
