using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="LearningReportCard"/>: the three panel titles and the time-filter bar. No proxy
/// is running, so <see cref="UsageStore"/> fails fast and the component falls back to <c>MockData</c>.
/// </summary>
public sealed class LearningReportCardTests
{
    private const string UnreachableAddress = "http://127.0.0.1:59988";

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new UsageStore(channelProvider: new NativeRouterChannelProvider(UnreachableAddress)));
        return ctx;
    }

    [Fact]
    public void Renders_spend_grade_mix_and_score_delta_panels_from_mock_data_when_unreachable()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<LearningReportCard>();

        cut.WaitForAssertion(assertion: () =>
        {
            cut.Markup.Should().Contain("Spend by Model");
            cut.Markup.Should().Contain("Grade Mix");
            cut.Markup.Should().Contain("Score Delta vs Frozen Policy");
            cut.Markup.Should().Contain("Demo data");
            cut.Markup.Should().Contain("A 42.0%");
        }, timeout: TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Renders_three_echart_hosts()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<LearningReportCard>();

        cut.WaitForAssertion(
            assertion: () => cut.FindAll("div[id^='echart-']").Should().HaveCount(3),
            timeout: TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Clicking_a_time_filter_switches_the_active_selection()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<LearningReportCard>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").GetAttribute("class").Should()
            .Contain("active");
    }
}
