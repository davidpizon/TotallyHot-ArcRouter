using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

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

    private static Conversation MakeLiveConversation()
    {
        return new Conversation(
            Id: "live-1",
            Title: "Live Session",
            FirstTimestamp: "10:00:00",
            LastTimestamp: "10:05:00",
            0.02m,
            500,
            100,
            false,
            Turns:
            [
                new ConversationTurn(Id: "live-1-t1", Agent: "A", Model: "gpt-4o-mini", 1, 500,
                    100, 80m, 0.02m, 1,
                    0m, 200, 10m, Timestamp: "10:00:00",
                    RoutingSteps: [], TimestampUtc: DateTimeOffset.UtcNow)
            ]);
    }

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new UsageStore(channelProvider: new NativeRouterChannelProvider(UnreachableAddress)));
        return ctx;
    }

    [Fact]
    public void Renders_the_default_routing_roi_metric_selected()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Routing ROI"), timeout: WaitTimeout);
        cut.WaitForAssertion(
            assertion: () =>
            {
                var link = cut.Find("a");
                link.TextContent.Trim().Should().Be("Methodology");
                link.GetAttribute("href").Should().Be(ProductDocs.ScoreDeltaMethodologyUrl);
                // .ds-doc-link, not a bare `underline` utility: app.css is a committed, tree-shaken
                // Tailwind build that ships no underline/underline-offset rule, so styling the
                // citation with utilities alone renders it as unclickable-looking plain text.
                link.ClassList.Should().Contain("ds-doc-link");
            },
            timeout: WaitTimeout);
    }

    [Fact]
    public async Task Methodology_link_is_hidden_when_routing_roi_is_not_the_active_metric()
    {
        await using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Turn Cost").Click());

        await cut.WaitForAssertionAsync(assertion: () => cut.Markup.Should().NotContain("Methodology"),
            timeout: WaitTimeout);
    }

    [Fact]
    public async Task Switching_metric_updates_the_chart_title()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));
        // InvokeAsync makes Find-then-Click atomic on the renderer's synchronization context: UsageStore's
        // background rollup-history load (this class's own remarks - it "fails fast" but not always
        // instantly) can re-render between a plain Find() and Click(), leaving Click() dispatching against
        // an event handler ID the re-render already invalidated (Bunit.Rendering.UnknownEventHandlerIdException).
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Turn Cost").Click());

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Stepped cumulative cost"),
            timeout: WaitTimeout);
    }

    [Fact]
    public async Task Switching_range_updates_the_range_caption()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));
        // See Switching_metric_updates_the_chart_title's remarks on why this is InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Day").Click());

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Past 24 hours"), timeout: WaitTimeout);
    }

    [Fact]
    public void Initial_session_id_parameter_pre_selects_the_session_dropdown()
    {
        using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.InitialSessionId, value: "live-1"));

        cut.WaitForAssertion(assertion: () => cut.Find("select").GetAttribute("value").Should().Be("live-1"),
            timeout: WaitTimeout);
    }

    [Fact]
    public void Live_conversation_options_are_listed_in_the_session_dropdown()
    {
        using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: conversations));

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Live Session"), timeout: WaitTimeout);
    }

    [Fact]
    public async Task Selecting_a_session_from_the_dropdown_scopes_the_chart()
    {
        using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: conversations));
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Live Session"), timeout: WaitTimeout);

        // See Switching_metric_updates_the_chart_title's remarks on why this is InvokeAsync-wrapped -
        // Change() dispatches an event the same way Click() does, so it races the same background
        // UsageStore load-failure continuation.
        await cut.InvokeAsync(() => cut.Find("select").Change("live-1"));

        cut.Find("select").GetAttribute("value").Should().Be("live-1");
    }

    [Fact]
    public void Renders_a_chart_when_the_metric_has_data_in_range()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));

        // MockData.BuildMetricHistory always fills the Week range (the default) once the (failed) rollup
        // load falls back to it, so a chart eventually renders.
        cut.WaitForAssertion(assertion: () => cut.FindAll("div[id^='echart-']").Should().NotBeEmpty(),
            timeout: WaitTimeout);
    }

    [Fact]
    public void Mock_backed_chart_is_labelled_demo_data()
    {
        using var ctx = CreateContext();

        // No live conversations and an unreachable router, so the corpus falls back to MockData. Routing
        // ROI then draws synthetic savings bars and a dollar headline that are indistinguishable from real
        // frozen-baseline measurements - beside a Methodology link vouching for how they were computed.
        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("demo data"), timeout: WaitTimeout);
    }

    [Fact]
    public void Real_conversation_data_is_not_labelled_demo_data()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: [MakeLiveConversation()]));

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().NotContain("demo data"), timeout: WaitTimeout);
    }
}