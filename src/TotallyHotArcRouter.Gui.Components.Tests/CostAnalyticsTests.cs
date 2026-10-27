using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

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
    private const string UnreachableAddress = "http://127.0.0.1:59990";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(4);

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
        ctx.Services.AddSingleton(new UsageStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress)));
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
        await using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));
        // InvokeAsync makes Find-then-Click atomic on the renderer's synchronization context: UsageStore's
        // background rollup-history load (this class's own remarks - it "fails fast" but not always
        // instantly) can re-render between a plain Find() and Click(), leaving Click() dispatching against
        // an event handler ID the re-render already invalidated (Bunit.Rendering.UnknownEventHandlerIdException).
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Turn Cost").Click());

        await cut.WaitForAssertionAsync(assertion: () => cut.Markup.Should().Contain("Stepped cumulative cost"),
            timeout: WaitTimeout);
    }

    [Fact]
    public async Task Switching_range_updates_the_range_caption()
    {
        await using var ctx = CreateContext();

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: []));
        // See Switching_metric_updates_the_chart_title's remarks on why this is InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Day").Click());

        await cut.WaitForAssertionAsync(assertion: () => cut.Markup.Should().Contain("Past 24 hours"),
            timeout: WaitTimeout);
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
        await using var ctx = CreateContext();

        var conversations = new[] { MakeLiveConversation() };
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: conversations));
        await cut.WaitForAssertionAsync(assertion: () => cut.Markup.Should().Contain("Live Session"),
            timeout: WaitTimeout);

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

    [Fact]
    public async Task Unknown_null_token_counts_and_cost_still_plot_as_zero()
    {
        // #176 §5.1 / §12: CostAnalytics coalesces null PromptTokens / CompletionTokens / TotalCost to 0
        // when building chart points, so unknown values keep today's plotted zeroes.
        await using var ctx = CreateContext();

        var conversations = new[]
        {
            new Conversation(
                Id: "null-priced",
                Title: "Unknown Metrics",
                FirstTimestamp: "10:00:00",
                LastTimestamp: "10:00:00",
                0m,
                0,
                0,
                false,
                Turns:
                [
                    new ConversationTurn(
                        Id: "null-priced-t1", Agent: "A", Model: "gpt-4o-mini", 1,
                        PromptTokens: null, CompletionTokens: null, 0m, TotalCost: null,
                        0, 0m, 0, 0m, Timestamp: "10:00:00", RoutingSteps: [],
                        TimestampUtc: DateTimeOffset.UtcNow, IsLive: true)
                ],
                UnpricedTurns: 1)
        };

        var cut = ctx.Render<CostAnalytics>(p =>
            p.Add(parameterSelector: c => c.Conversations, value: conversations));

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Turn Cost").Click());
        await cut.WaitForAssertionAsync(
            assertion: () => cut.Find(".metric-accent").TextContent.Trim().Should().Be("$0.00"),
            timeout: WaitTimeout);

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Tokens").Click());
        await cut.WaitForAssertionAsync(
            assertion: () => cut.Find(".metric-accent").TextContent.Trim().Should().Be("0"),
            timeout: WaitTimeout);
    }

    [Fact]
    public async Task EpochTooltip_Click_ShowsTheSameLines()
    {
        await using var ctx = EpochContext(out var calls);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);

        await cut.InvokeAsync(() => cut.FindComponent<EChart>().Instance.OnEpochClick(42));

        Detail(cut).Should().Contain("Time (UTC): 2026-10-26 15:04:05").And.Contain("This mark does not say it caused the line to move.");
        calls.ChartCalls.Should().Be(1);
    }

    [Fact]
    public async Task EpochTooltip_Keyboard_ShowsTheSameLines()
    {
        await using var ctx = EpochContext(out _);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);

        await cut.InvokeAsync(() => cut.Find("[data-epoch-marks] button").Click());

        Detail(cut).Should().Contain("Time (UTC): 2026-10-26 15:04:05").And.Contain("Local grader took over");
    }

    [Fact]
    public async Task EpochTooltip_Touch_UsesTheSameClickLines()
    {
        await using var ctx = EpochContext(out _);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);

        await cut.InvokeAsync(() => cut.FindComponent<EChart>().Instance.OnEpochClick(42));

        Detail(cut).Should().Contain("Time (UTC): 2026-10-26 15:04:05").And.Contain("This mark does not say it caused the line to move.");
    }

    [Fact]
    public async Task UnitSwitch_DefaultsToTokensPerDollar()
    {
        await using var ctx = EpochContext(out _);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);

        var toggle = cut.Find("[role='switch']");
        toggle.GetAttribute("aria-checked").Should().Be("false");
        ModelJson(cut).Should().Contain("tok/$").And.Contain("Up is better");
    }

    [Fact]
    public async Task UnitSwitch_FlipsAxisAndTooltip()
    {
        await using var ctx = EpochContext(out var calls);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);
        var before = calls.ChartCalls;

        await cut.InvokeAsync(() => cut.Find("[role='switch']").Click());

        cut.Find("[role='switch']").GetAttribute("aria-checked").Should().Be("true");
        ModelJson(cut).Should().Contain("$/1k tok").And.Contain("Down is better").And.Contain("2.5");
        calls.ChartCalls.Should().Be(before);
    }

    [Fact]
    public async Task UnitSwitch_BlanksStayBlankInBothViews()
    {
        await using var ctx = EpochContext(out _);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);

        GapHasNoValue(ModelJson(cut)).Should().BeTrue();
        await cut.InvokeAsync(() => cut.Find("[role='switch']").Click());
        GapHasNoValue(ModelJson(cut)).Should().BeTrue();
    }

    [Fact]
    public async Task UnitSwitch_EpochTimesStayPut()
    {
        await using var ctx = EpochContext(out var calls);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);
        var before = EpochTime(ModelJson(cut));
        var chartCalls = calls.ChartCalls;

        await cut.InvokeAsync(() => cut.Find("[role='switch']").Click());

        EpochTime(ModelJson(cut)).Should().Be(before);
        calls.ChartCalls.Should().Be(chartCalls);
    }

    [Fact]
    public async Task UnitSwitch_OnlyOnTheTokenEfficiencyPill()
    {
        await using var ctx = EpochContext(out _);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await cut.WaitForAssertionAsync(assertion: () => cut.FindAll("[role='switch']").Should().BeEmpty(), timeout: WaitTimeout);

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Turn Cost").Click());
        cut.FindAll("[role='switch']").Should().BeEmpty();

        await OpenTokenEfficiency(cut);
        cut.FindAll("[role='switch']").Should().ContainSingle();

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Routing ROI").Click());
        cut.FindAll("[role='switch']").Should().BeEmpty();
        cut.FindAll("[data-epoch-legend]").Should().BeEmpty();
        cut.FindAll("[data-epoch-marks]").Should().BeEmpty();
        cut.FindAll("[data-epoch-detail]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Grader epoch marks are unavailable.");
    }

    [Fact]
    public async Task UnitSwitch_VisibleLabel_KeyboardButton()
    {
        await using var ctx = EpochContext(out _);
        var cut = ctx.Render<CostAnalytics>(p => p.Add(parameterSelector: c => c.Conversations, value: []));
        await OpenTokenEfficiency(cut);

        var toggle = cut.Find("button[role='switch']");
        toggle.TextContent.Should().Contain("Tokens per dollar").And.Contain("Up is better")
            .And.Contain("Dollars per 1,000 tokens").And.Contain("Down is better");
        cut.Find("[data-epoch-marks] button").GetAttribute("style").Should().Contain("min-height:44px");
    }

    private static BunitContext EpochContext(out EpochCalls calls)
    {
        calls = new EpochCalls();
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new UsageStore(client: new UsageQueryClient(calls)));
        return ctx;
    }

    private static async Task OpenTokenEfficiency(IRenderedComponent<CostAnalytics> cut)
    {
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Token efficiency").Click());
        await cut.WaitForAssertionAsync(
            assertion: () => cut.FindAll("[role='switch']").Should().NotBeEmpty(),
            timeout: WaitTimeout);
    }

    private static string Detail(IRenderedComponent<CostAnalytics> cut)
    {
        return cut.Find("[data-epoch-detail]").TextContent;
    }

    private static string ModelJson(IRenderedComponent<CostAnalytics> cut)
    {
        return cut.FindComponent<EChart>().Instance.ModelJson;
    }

    private static bool GapHasNoValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        var gap = document.RootElement.GetProperty("efficiency").EnumerateArray()
            .Single(point => point.GetProperty("gap").GetBoolean());
        return !gap.TryGetProperty(propertyName: "value", value: out _);
    }

    private static long EpochTime(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("epochs")[0].GetProperty("t").GetInt64();
    }

    private sealed class EpochCalls : Contract.UsageAdminService.UsageAdminServiceClient
    {
        public int ChartCalls { get; private set; }

        public override AsyncUnaryCall<Contract.UsageRollupResponse> GetUsageRollupAsync(
            Contract.GetUsageRollupRequest request, CallOptions options)
        {
            return Done(new Contract.UsageRollupResponse());
        }

        public override AsyncUnaryCall<Contract.RoutingRoiResponse> GetRoutingRoiAsync(
            Contract.GetRoutingRoiRequest request, CallOptions options)
        {
            return Done(new Contract.RoutingRoiResponse());
        }

        public override AsyncUnaryCall<Contract.GraderEpochChartResponse> GetGraderEpochChartAsync(
            Contract.GetGraderEpochChartRequest request, CallOptions options)
        {
            ChartCalls++;
            var chart = new Contract.GraderEpochChartResponse();
            chart.Points.Add(new Contract.GraderEpochEfficiencyPoint
            {
                BucketStartUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero)),
                TokensPerDollar = "400",
                DollarsPerThousandTokens = "2.5",
                KnownRows = 1
            });
            chart.Points.Add(new Contract.GraderEpochEfficiencyPoint
            {
                BucketStartUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero)),
                KnownRows = 0,
                SkippedRows = 1
            });
            chart.Markers.Add(new Contract.GraderEpochMarker
            {
                Id = 42,
                AtUtc = "2026-10-26T15:04:05.0000000+00:00",
                Kind = "code",
                EpochType = "local_takeover",
                Cause = "takeover"
            });
            return Done(chart);
        }

        private static AsyncUnaryCall<T> Done<T>(T response)
        {
            return new AsyncUnaryCall<T>(
                responseAsync: Task.FromResult(response),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}