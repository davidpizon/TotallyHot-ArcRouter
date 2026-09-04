using AwesomeAssertions;
using Bunit;
using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="RegretHarnessAdmin"/>: the Governance tab's Regret Harness panel. Driven through
/// a fake <see cref="IRegretHarnessAdminClient"/> so nothing here needs a live proxy or a gRPC channel.
/// </summary>
public sealed class RegretHarnessAdminTests
{
    private static BunitContext NewContext(IRegretHarnessAdminClient client)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new RegretHarnessAdminStore(client));
        return ctx;
    }

    private static RegretHarnessStatusInfo NoRunYet()
    {
        return new RegretHarnessStatusInfo(false, null, null, []);
    }

    private static RegretHarnessStatusInfo Completed()
    {
        return new RegretHarnessStatusInfo(
            true,
            RanAtUtc: new DateTimeOffset(2026, 8, 25, 12, 0, 0, offset: TimeSpan.Zero),
            Message: "Completed: 2919 ID-test task(s), 176 OOD task(s) replayed.",
            Splits:
            [
                new RegretHarnessSplitReportInfo("ID test", "| Router | CumReg |\n|---|---:|\n| dim_best | 244.3459 |")
            ]);
    }

    [Fact]
    public void No_run_yet_renders_the_empty_state_and_run_button()
    {
        using var ctx = NewContext(new FakeClient(NoRunYet()));

        var cut = ctx.Render<RegretHarnessAdmin>();

        cut.Markup.Should().Contain("No run yet this session");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Run");
    }

    [Fact]
    public void A_completed_run_renders_its_message_and_split_reports()
    {
        using var ctx = NewContext(new FakeClient(Completed()));

        var cut = ctx.Render<RegretHarnessAdmin>();

        cut.Markup.Should().Contain("Completed: 2919 ID-test task(s), 176 OOD task(s) replayed.");
        cut.Markup.Should().Contain("ID test");
        cut.Markup.Should().Contain("dim_best");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(new FakeClient
        { Error = new RegretHarnessAdminException(message: "nope", isUnavailable: true) });

        var cut = ctx.Render<RegretHarnessAdmin>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void Retry_reloads_after_the_router_becomes_reachable()
    {
        var client = new FakeClient { Error = new RegretHarnessAdminException(message: "nope", isUnavailable: true) };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RegretHarnessAdmin>();
        cut.Markup.Should().Contain("Router unreachable");

        client.Error = null;
        client.Status = NoRunYet();
        cut.Find("button").Click();

        cut.Markup.Should().Contain("No run yet this session");
    }

    [Fact]
    public void Clicking_run_streams_stage_progress_then_shows_the_outcome()
    {
        var client = new FakeClient(NoRunYet())
        {
            RunEvents =
            [
                new RegretHarnessRunEvent(StageProgress: RegretHarnessStageInfo.LoadingCorpus, null),
                new RegretHarnessRunEvent(
                    null,
                    Result: new RegretHarnessRunResultInfo(Kind: RegretHarnessRunResultKindInfo.Completed,
                        Message: "Completed: 1 task(s).", RanAtUtc: DateTimeOffset.UtcNow,
                        Splits: [new RegretHarnessSplitReportInfo("OOD", "| Router |")]))
            ]
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RegretHarnessAdmin>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Run").Click();

        cut.Markup.Should().Contain("Completed: 1 task(s).");
        cut.Markup.Should().Contain("OOD");
    }

    [Fact]
    public async Task Running_renders_the_current_stage_label()
    {
        // The result event is gated so the test can observe the in-progress stage label before releasing it.
        var gate = new TaskCompletionSource<bool>();
        var client = new FakeClient(NoRunYet())
        {
            Gate = gate,
            RunEvents =
            [
                new RegretHarnessRunEvent(StageProgress: RegretHarnessStageInfo.BuildingKnnIndex, null),
                new RegretHarnessRunEvent(
                    null,
                    Result: new RegretHarnessRunResultInfo(Kind: RegretHarnessRunResultKindInfo.Completed,
                        Message: "Completed.", RanAtUtc: DateTimeOffset.UtcNow, Splits: []))
            ]
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RegretHarnessAdmin>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Run").Click();
        cut.WaitForState(() => cut.Markup.Contains("Embedding the OOD split"));

        cut.Markup.Should().Contain("Embedding the OOD split");

        gate.SetResult(true);
        cut.WaitForState(() => cut.Markup.Contains("Completed."));
        await Task.CompletedTask;
    }

    private sealed class FakeClient(RegretHarnessStatusInfo? status = null) : IRegretHarnessAdminClient
    {
        public RegretHarnessStatusInfo? Status { get; set; } = status;

        public RegretHarnessAdminException? Error { get; set; }

        public IReadOnlyList<RegretHarnessRunEvent> RunEvents { get; set; } = [];

        /// <summary>
        /// Optional gate awaited immediately before yielding a <see cref="RegretHarnessRunEvent.Result"/>
        /// event, so a test can observe the streaming-but-not-yet-final state before releasing it.
        /// </summary>
        public TaskCompletionSource<bool>? Gate { get; set; }

        public Task<RegretHarnessStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Error is not null
                ? Task.FromException<RegretHarnessStatusInfo>(Error)
                : Task.FromResult(Status!);
        }

        public async IAsyncEnumerable<RegretHarnessRunEvent> RunAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Error is not null) throw Error;

            foreach (var runEvent in RunEvents)
            {
                if (runEvent.Result is not null && Gate is not null) await Gate.Task;

                yield return runEvent;
            }
        }
    }
}
