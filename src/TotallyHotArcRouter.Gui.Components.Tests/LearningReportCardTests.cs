using AwesomeAssertions;
using Bunit;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="LearningReportCard"/>: the three panel titles, the time-filter bar, and the
/// reload generation guard. Most tests run with no proxy, so <see cref="UsageStore"/> fails fast and the
/// component falls back to <c>MockData</c>; the guard test stages out-of-order responses through a fake client.
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
        }, timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Renders_three_echart_hosts()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<LearningReportCard>();

        cut.WaitForAssertion(
            assertion: () => cut.FindAll("div[id^='echart-']").Should().HaveCount(3),
            timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Clicking_a_time_filter_switches_the_active_selection()
    {
        using var ctx = CreateContext();

        var cut = ctx.Render<LearningReportCard>();
        // InvokeAsync makes Find-then-Click atomic on the renderer's synchronization context: the initial
        // reload against the unreachable endpoint can re-render between a plain FindAll() and Click(),
        // invalidating the handler ID Click() dispatches against (Bunit.Rendering.UnknownEventHandlerIdException).
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Year").Click());

        await cut.WaitForAssertionAsync(
            assertion: () => cut.FindAll("button").First(b => b.TextContent.Trim() == "Year")
                .GetAttribute("class").Should().Contain("active"),
            timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_superseded_filter_response_arriving_last_is_not_applied()
    {
        var stub = new DeferredReportCardClient();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new UsageStore(client: new UsageQueryClient(stub)));

        var cut = ctx.Render<LearningReportCard>();
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Day").Click());
        stub.Pending.Should().HaveCount(2, because: "the initial Month load and the Day reload are both in flight");

        // The newer (Day) request answers first, then the older (Month) one straggles in.
        stub.Pending[1].SetResult(Card(scored: 7));
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("7 scored"),
            timeout: TimeSpan.FromSeconds(5));

        stub.Pending[0].SetResult(Card(scored: 99));

        // The stale continuation hops through the thread pool (UsageStore awaits with ConfigureAwait(false))
        // before posting back to the renderer, so a single dispatcher drain can run too early. Watch for a
        // bounded window instead: without the guard the stale render lands within a few milliseconds.
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(millisecondsDelay: 10, cancellationToken: Xunit.TestContext.Current.CancellationToken);
            await cut.InvokeAsync(() => { });
            cut.Markup.Should().Contain("7 scored").And.NotContain("99 scored");
        }
    }

    private static Contract.LearningReportCardResponse Card(int scored)
    {
        return new Contract.LearningReportCardResponse { ScoredRequests = scored, TotalSpendUsd = "0" };
    }

    /// <summary>
    /// A usage-admin client whose report-card calls stay pending until the test completes them, in
    /// whatever order it chooses, so out-of-order responses can be staged deterministically.
    /// </summary>
    private sealed class DeferredReportCardClient : Contract.UsageAdminService.UsageAdminServiceClient
    {
        public List<TaskCompletionSource<Contract.LearningReportCardResponse>> Pending { get; } = [];

        public override AsyncUnaryCall<Contract.LearningReportCardResponse> GetLearningReportCardAsync(
            Contract.GetLearningReportCardRequest request, CallOptions options)
        {
            var pending = new TaskCompletionSource<Contract.LearningReportCardResponse>();
            Pending.Add(pending);
            return new AsyncUnaryCall<Contract.LearningReportCardResponse>(
                responseAsync: pending.Task,
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}
