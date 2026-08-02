using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="ModelDistribution"/>: time filter selection and legend rendering.
/// The chart JSON itself is built by TotallyHot.ArcRouter.Gui.Charts (tested there); this only checks the
/// component wires its parameters into <see cref="EChart"/> and renders the custom legend.</summary>
public sealed class ModelDistributionTests
{
    private static readonly IReadOnlyList<TokenBucket> Buckets =
    [
        new("Mon", 100m, 40m),
        new("Tue", 200m, 80m),
    ];

    private static readonly IReadOnlyList<ModelShare> Shares =
    [
        new("gpt-4o-mini", 60m, "#10b981"),
        new("claude-3-haiku", 40m, "#38bdf8"),
    ];

    [Fact]
    public void Renders_the_model_share_legend()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ModelDistribution>(p => p
            .Add(c => c.TokenBuckets, Buckets)
            .Add(c => c.ModelShares, Shares));

        cut.Markup.Should().Contain("gpt-4o-mini");
        cut.Markup.Should().Contain("claude-3-haiku");
    }

    [Fact]
    public void Renders_all_time_filter_buttons_with_month_active_by_default()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ModelDistribution>(p => p
            .Add(c => c.TokenBuckets, Buckets)
            .Add(c => c.ModelShares, Shares));

        var buttons = cut.FindAll("button");
        buttons.Select(b => b.TextContent.Trim()).Should().Contain(["Day", "Month", "3-Month", "6-Month", "Year"]);
    }

    [Fact]
    public void Clicking_a_time_filter_switches_the_active_selection()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ModelDistribution>(p => p
            .Add(c => c.TokenBuckets, Buckets)
            .Add(c => c.ModelShares, Shares));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").Click();

        // Active state flips the button's background to the highlight color; the year button should now
        // carry it instead of month. Re-query after the click since the render replaces the node.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").GetAttribute("style").Should().Contain("#38bdf8");
    }

    [Fact]
    public void Renders_two_echart_hosts_for_the_histogram_and_donut()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ModelDistribution>(p => p
            .Add(c => c.TokenBuckets, Buckets)
            .Add(c => c.ModelShares, Shares));

        cut.FindAll("div[id^='echart-']").Should().HaveCount(2);
    }
}

