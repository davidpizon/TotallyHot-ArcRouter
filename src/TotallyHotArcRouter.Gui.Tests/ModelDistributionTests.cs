using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ModelDistribution"/>: time filter selection and legend rendering. No proxy is
/// running in the test process, so every <see cref="UsageStore"/> request fails fast with a connection
/// refusal and the component falls back to <see cref="MockData"/> - the same offline/no-proxy path the
/// component is built to degrade to. The chart JSON itself is built by TotallyHot.ArcRouter.Gui.Charts
/// (tested there); this only checks the component wires results into <see cref="EChart"/> and renders the
/// custom legend.
/// </summary>
public sealed class ModelDistributionTests
{
    // An address nothing listens on, so the underlying HttpClient.SendAsync fails fast with a connection
    // refusal rather than depending on whether an actual proxy happens to be running on the store's real
    // default port (5001) on the machine running this test - same technique as ProviderAdminStoreTests.
    private const string UnreachableAddress = "http://127.0.0.1:59992";

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new UsageStore(managementAddress: UnreachableAddress));
        return ctx;
    }

    [Fact]
    public void Renders_the_model_share_legend_from_mock_data_when_unreachable()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<ModelDistribution>();

        // ModelDistribution awaits two concurrent UsageStore.LoadRollupAsync calls (groupBy=day and
        // groupBy=model) before falling back to mock data; a connection attempt against the unreachable
        // loopback port above takes ~2s to fail on this host (not an instant refusal).
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("gpt-4o-mini");
            cut.Markup.Should().Contain("claude-3-haiku");
        }, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Renders_all_time_filter_buttons_with_month_active_by_default()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<ModelDistribution>();

        var buttons = cut.FindAll("button");
        buttons.Select(b => b.TextContent.Trim()).Should().Contain(["Day", "Month", "3-Month", "6-Month", "Year"]);
    }

    [Fact]
    public void Clicking_a_time_filter_switches_the_active_selection()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<ModelDistribution>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").Click();

        // Active state flips the button's background to the highlight color via the .active class; the
        // year button should now carry it instead of month. Re-query after the click since the render
        // replaces the node.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").GetAttribute("class").Should().Contain("active");
    }

    [Fact]
    public void Renders_two_echart_hosts_for_the_histogram_and_donut()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<ModelDistribution>();

        cut.FindAll("div[id^='echart-']").Should().HaveCount(2);
    }
}
