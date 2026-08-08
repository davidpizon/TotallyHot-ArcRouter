using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="CostAnalytics"/>: metric/range/session selection and how they drive the
/// control bar and chart header. The chart model math itself lives in and is tested by
/// TotallyHot.ArcRouter.Gui.Charts.CostChartBuilder; this only checks the component wires selections into it
/// and re-renders. No proxy is running in the test process, so <see cref="UsageStore"/>'s rollup-history
/// fetch fails fast (a connection refusal against the unreachable loopback port below) and the component
/// falls back to <see cref="MockData"/> - assertions that depend on that first load having completed use
/// <c>WaitForAssertion</c>, since it finishes on a background continuation, not within the initial render.
/// </summary>
public sealed class CostAnalyticsTests
{
    // An address nothing listens on; a connection attempt against it takes a couple of seconds to fail on
    // this host (not an instant refusal), which is why assertions gated on the load use a generous wait.
    private const string UnreachableAddress = "http://127.0.0.1:59990";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(6);

    private static Conversation MakeLiveConversation() => new(
        Id: "live-1",
        Title: "Live Session",
        FirstTimestamp: "10:00:00",
        LastTimestamp: "10:05:00",
        TotalCost: 0.02m,
        TotalPromptTokens: 500,
        TotalCompletionTokens: 100,
        HasFallbackTurns: false,
        Turns:
        [
            new(Id: "live-1-t1", Agent: "A", Model: "gpt-4o-mini", TurnNumber: 1, PromptTokens: 500,
                CompletionTokens: 100, RoutingRoi: 80m, TotalCost: 0.02m, ToolExecutionSteps: 1,
                CacheHitRate: 0m, TimeToFirstTokenMs: 200, ContextBufferPercent: 10m, Timestamp: "10:00:00",
                RoutingSteps: [], TimestampUtc: DateTimeOffset.UtcNow),
        ]);

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new UsageStore(managementAddress: UnreachableAddress));
        return ctx;
    }

    [Fact]
    public void Renders_the_default_routing_roi_metric_selected()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p => p.Add(c => c.Conversations, Array.Empty<Conversation>()));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Routing ROI"), WaitTimeout);
    }

    [Fact]
    public void Switching_metric_updates_the_chart_title()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p => p.Add(c => c.Conversations, Array.Empty<Conversation>()));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Turn Cost").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Stepped cumulative cost"), WaitTimeout);
    }

    [Fact]
    public void Switching_range_updates_the_range_caption()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p => p.Add(c => c.Conversations, Array.Empty<Conversation>()));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Day").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Past 24 hours"), WaitTimeout);
    }

    [Fact]
    public void Initial_session_id_parameter_pre_selects_the_session_dropdown()
    {
        using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.InitialSessionId, "live-1"));

        cut.WaitForAssertion(() => cut.Find("select").GetAttribute("value").Should().Be("live-1"), WaitTimeout);
    }

    [Fact]
    public void Live_conversation_options_are_listed_in_the_session_dropdown()
    {
        using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p.Add(c => c.Conversations, conversations));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Live Session"), WaitTimeout);
    }

    [Fact]
    public void Selecting_a_session_from_the_dropdown_scopes_the_chart()
    {
        using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p.Add(c => c.Conversations, conversations));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Live Session"), WaitTimeout);

        cut.Find("select").Change("live-1");

        cut.Find("select").GetAttribute("value").Should().Be("live-1");
    }

    [Fact]
    public void Renders_a_chart_when_the_metric_has_data_in_range()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p => p.Add(c => c.Conversations, Array.Empty<Conversation>()));

        // MockData.BuildMetricHistory always fills the Week range (the default) once the (failed) rollup
        // load falls back to it, so a chart eventually renders.
        cut.WaitForAssertion(() => cut.FindAll("div[id^='echart-']").Should().NotBeEmpty(), WaitTimeout);
    }
}
