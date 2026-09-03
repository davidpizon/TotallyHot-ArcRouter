using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="BenchmarkData"/>: the Governance tab's Benchmark Data panel. Driven through a fake
/// <see cref="IBenchmarkDataAdminClient"/> so nothing here needs a live proxy or a gRPC channel, mirroring
/// <c>PriceSourcesAdminTests</c>.
/// </summary>
public sealed class BenchmarkDataTests
{
    private static BunitContext NewContext(IBenchmarkDataAdminClient client)
    {
        return NewContext(client: client, voterClient: new FakeVoterClient());
    }

    private static BunitContext NewContext(IBenchmarkDataAdminClient client, ILlmRouterModelAdminClient voterClient)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new BenchmarkDataStore(client));
        ctx.Services.AddSingleton(new LlmRouterModelStore(voterClient));
        return ctx;
    }

    [Fact]
    public void Section_header_reads_task_matrix()
    {
        using var ctx = NewContext(new FakeClient(state: BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo(FileName: "models.json", true, 1_400, 3, SyncedAtUtc: DateTimeOffset.UtcNow)));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Task Matrix");
        cut.Markup.Should().NotContain("CodeRouterBench Corpus");
    }

    [Fact]
    public void Exactly_one_task_matrix_card_renders_regardless_of_file_count()
    {
        var files = Enumerable.Range(1, 8)
            .Select(i =>
                new BenchmarkFileStatusInfo(FileName: $"file{i}.json", true, 10, 1, SyncedAtUtc: DateTimeOffset.UtcNow))
            .ToArray();
        using var ctx = NewContext(new FakeClient(state: BenchmarkDataAdminState.Current, files: files));

        var cut = ctx.Render<BenchmarkData>();

        // One Task Matrix card regardless of how many corpus files it lists, plus one Local Voter Model
        // card from the always-present voter section (see NewContext's default FakeVoterClient).
        cut.FindAll(".ds-surface-card-draggable").Should().HaveCount(2);
    }

    [Fact]
    public void Current_state_renders_a_disabled_update_button_and_the_file_is_hidden_behind_the_disclosure()
    {
        var syncedAt = DateTimeOffset.UtcNow.AddHours(-1);
        using var ctx = NewContext(new FakeClient(state: BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo(FileName: "models.json", true, 1_400, 3, SyncedAtUtc: syncedAt)));

        var cut = ctx.Render<BenchmarkData>();

        // Collapsed no longer means "absent from the DOM": the pane stays mounted so it can animate
        // shut as well as open, so the state to assert is the wrapper's (no .open, plus inert).
        var disclosure = cut.FindAll(".ls-disclosure")
            .Single(d => d.InnerHtml.Contains(value: "models.json", comparisonType: StringComparison.Ordinal));
        disclosure.ClassList.Should().NotContain("open");
        disclosure.HasAttribute("inert").Should().BeTrue();

        // The per-card button is disabled once current - there is nothing for it to update - and
        // re-verifying against Hugging Face is now the top-of-page Resync button's job.
        var button = cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeTrue();

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Show", comparisonType: StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("models.json");
        cut.Markup.Should().Contain("3 rows");
    }

    [Fact]
    public void Update_state_renders_an_enabled_update_button()
    {
        using var ctx = NewContext(new FakeClient(state: BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null)));

        var cut = ctx.Render<BenchmarkData>();
        var button = cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeFalse();

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Show", comparisonType: StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("Never synced");
    }

    [Fact]
    public void The_disclosure_starts_collapsed_and_reveals_every_file_when_toggled()
    {
        var files = Enumerable.Range(1, 8)
            .Select(i =>
                new BenchmarkFileStatusInfo(FileName: $"file{i}.json", true, 10, 1, SyncedAtUtc: DateTimeOffset.UtcNow))
            .ToArray();
        using var ctx = NewContext(new FakeClient(state: BenchmarkDataAdminState.Current, files: files));

        var cut = ctx.Render<BenchmarkData>();

        var toggle = cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Show 8 files", comparisonType: StringComparison.Ordinal));
        toggle.GetAttribute("aria-expanded").Should().Be("false");

        // The rows are rendered while collapsed - that is what lets the pane animate shut instead of
        // vanishing - so "collapsed" is the wrapper being closed and inert, not the rows being absent.
        var disclosure = cut.FindAll(".ls-disclosure")
            .Single(d => d.InnerHtml.Contains(value: "file1.json", comparisonType: StringComparison.Ordinal));
        disclosure.ClassList.Should().NotContain("open");
        disclosure.HasAttribute("inert").Should().BeTrue();

        toggle.Click();

        cut.FindAll("button").First(b =>
                b.TextContent.Contains(value: "Hide files", comparisonType: StringComparison.Ordinal))
            .GetAttribute("aria-expanded").Should().Be("true");
        disclosure = cut.FindAll(".ls-disclosure")
            .Single(d => d.InnerHtml.Contains(value: "file1.json", comparisonType: StringComparison.Ordinal));
        disclosure.ClassList.Should().Contain("open");
        disclosure.HasAttribute("inert").Should().BeFalse();
        foreach (var file in files) cut.Markup.Should().Contain(file.FileName);
    }

    [Fact]
    public async Task Two_progress_bars_render_only_while_syncing_and_the_lower_one_names_the_current_file()
    {
        var tcs = new TaskCompletionSource();
        var client = new FakeClient(state: BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null))
        {
            SyncEvents =
            [
                new BenchmarkSyncEvent(
                    Plan: new BenchmarkSyncPlanInfo(
                        Files: [new BenchmarkSyncPlanFileInfo(FileName: "models.json", 100)], 100),
                    null,
                    null),
                new BenchmarkSyncEvent(
                    null,
                    Progress: new BenchmarkSyncProgressInfo(FileName: "models.json",
                        Stage: BenchmarkSyncStageInfo.Downloading, 40, null, null, 100),
                    null)
            ],
            HoldBeforeFinalStatus = tcs.Task
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<BenchmarkData>();

        cut.FindAll("[role=progressbar]").Should().BeEmpty();

        // Click() only dispatches the event; RunAction's async continuation (parked on
        // HoldBeforeFinalStatus below) keeps running after Click() returns, so the two progress bars
        // and their later disappearance are asserted via WaitForAssertion rather than by awaiting Click().
        await cut.InvokeAsync(() =>
            cut.FindAll("button")
                .First(b => b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().HaveCount(2));
        cut.Markup.Should().Contain("models.json");

        tcs.SetResult();

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().BeEmpty());
    }

    [Fact]
    public void CheckFailed_state_renders_the_reason_and_a_disabled_button()
    {
        using var ctx =
            NewContext(new FakeClient(state: BenchmarkDataAdminState.CheckFailed, Reason: "failed to connect"));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Check Failed");
        cut.Markup.Should().Contain("failed to connect");
        // The per-card button stays disabled (an unknown freshness is not "an update is available"); the
        // label still surfaces "Check Failed" rather than a plain "Update" so the reason is visible.
        var button = cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Check Failed", comparisonType: StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Clicking_update_runs_a_sync_and_becomes_current()
    {
        var client = new FakeClient(state: BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null));
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal)).Click();

        client.SyncCount.Should().Be(1);
        // Once current, the button keeps its "Update" label but disables itself rather than exposing a
        // clickable "Re-verify"/"Current" affordance.
        var button = cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Resyncing_a_current_corpus_runs_a_sync_rather_than_a_recheck()
    {
        // The individual per-card button disables itself once current, so this "Current -> sync, not
        // recheck" behavior (re-running the sync is the only way to confirm the ledger's recorded
        // checksums still match what's on disk) is now only reachable through the top Resync button.
        var client = new FakeClient(state: BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo(FileName: "models.json", true, 1_400, 3, SyncedAtUtc: DateTimeOffset.UtcNow));
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal)).Click();

        client.SyncCount.Should().Be(1);
        client.RecheckCount.Should().Be(0);
    }

    [Fact]
    public void Resyncing_a_checkfailed_corpus_rechecks_rather_than_syncing_blind()
    {
        // Same reasoning as Resyncing_a_current_corpus_runs_a_sync_rather_than_a_recheck: the per-card
        // button is disabled in CheckFailed, so retrying the check now goes through Resync.
        var client = new FakeClient(state: BenchmarkDataAdminState.CheckFailed, Reason: "boom");
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal)).Click();

        client.RecheckCount.Should().Be(1);
        client.SyncCount.Should().Be(0);
    }

    [Fact]
    public void Resync_runs_both_cards_operations_together()
    {
        var client = new FakeClient(state: BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null));
        var voterClient = new FakeVoterClient
        {
            Files = [new LlmRouterModelFileStatusInfo(FileName: "model.onnx", false, 0, null, false, false)]
        };
        using var ctx = NewContext(client: client, voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal)).Click();

        client.SyncCount.Should().Be(1);
        voterClient.SyncCount.Should().Be(1);
    }

    [Fact]
    public async Task Resync_is_disabled_while_either_card_is_syncing()
    {
        var tcs = new TaskCompletionSource();
        var client = new FakeClient(state: BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null))
        {
            HoldBeforeFinalStatus = tcs.Task
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<BenchmarkData>();

        await cut.InvokeAsync(() =>
            cut.FindAll("button")
                .First(b => b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() =>
            cut.FindAll("button").First(b =>
                    b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal))
                .HasAttribute("disabled").Should().BeTrue());

        tcs.SetResult();

        cut.WaitForAssertion(() =>
            cut.FindAll("button").First(b =>
                    b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal))
                .HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void A_failed_files_error_is_rendered_on_its_card()
    {
        var client = new FakeClient(state: BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null))
        {
            SyncEvents =
            [
                new BenchmarkSyncEvent(
                    null,
                    Progress: new BenchmarkSyncProgressInfo(FileName: "models.json",
                        Stage: BenchmarkSyncStageInfo.Failed, null, null, Error: "checksum mismatch"),
                    null),
                new BenchmarkSyncEvent(null, null, FinalStatus: new BenchmarkDataStatusInfo(
                    State: BenchmarkDataAdminState.Update, null, CheckedAtUtc: DateTimeOffset.UtcNow,
                    Files: [new BenchmarkFileStatusInfo(FileName: "models.json", false, 0, 0, null)]))
            ]
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal)).Click();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Show", comparisonType: StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("checksum mismatch");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_is_down()
    {
        using var ctx = NewContext(new FakeClient
        {
            StatusError = new BenchmarkDataAdminException(message: "the router is not reachable.", isUnavailable: true)
        });

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void The_unreachable_state_names_no_endpoint_when_the_store_cannot_know_one()
    {
        // A store built over a caller-supplied client has no endpoint of its own, so naming the default
        // address would assert an endpoint this client may never have been pointed at.
        using var ctx = NewContext(new FakeClient
        {
            StatusError = new BenchmarkDataAdminException(message: "the router is not reachable.", isUnavailable: true)
        });

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Could not reach the router.");
        cut.Markup.Should().NotContain(TelemetryChannelFactory.DefaultServerAddress);
    }

    [Fact]
    public void The_store_reports_the_endpoint_it_was_pointed_at()
    {
        // Constructing a channel does not connect, so this stays offline; the panel reads this property to
        // name the address it actually failed to reach.
        using var store = new BenchmarkDataStore(null, serverAddress: "https://localhost:65111");

        store.ServerAddress.Should().Be("https://localhost:65111");
    }

    [Fact]
    public void The_voter_unreachable_state_offers_a_retry()
    {
        // A store built over a caller-supplied client (as NewContext does) has no endpoint of its own,
        // mirroring The_unreachable_state_names_no_endpoint_when_the_store_cannot_know_one above - so this
        // covers the Retry affordance rather than the address text, which the corpus test already covers.
        var voterClient = new FakeVoterClient
        {
            StatusError = new LlmRouterModelAdminException(message: "the router is not reachable.", isUnavailable: true)
        };
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Could not reach the router.");

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Retry", comparisonType: StringComparison.Ordinal)).Click();

        voterClient.GetStatusCount.Should().Be(2);
    }

    [Fact]
    public void Voter_current_state_renders_a_disabled_update_button()
    {
        // The voter section's own button (last "Update" button in the DOM, after the Task Matrix panel's)
        // disables itself once current - there is nothing for it to update. Re-running the sync to
        // re-verify already-cached files (checksum verification is in-memory only and doesn't survive a
        // process restart) is now the top Resync button's job.
        var voterClient = new FakeVoterClient();
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();

        var button = cut.FindAll("button")
            .Last(b => b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Resyncing_a_current_voter_model_reruns_sync()
    {
        var voterClient = new FakeVoterClient();
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal)).Click();

        voterClient.SyncCount.Should().Be(1);
    }

    [Fact]
    public void Exactly_one_voter_card_renders_regardless_of_file_count()
    {
        var files = Enumerable.Range(1, 5)
            .Select(i => new LlmRouterModelFileStatusInfo(FileName: $"file{i}.bin", true, 10,
                SyncedAtUtc: DateTimeOffset.UtcNow, true, false))
            .ToArray();
        var voterClient = new FakeVoterClient { Files = files };
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();

        // One card for the Task Matrix section (no files there) plus one for Local Voter Model.
        cut.FindAll(".ds-surface-card-draggable").Should().HaveCount(2);
    }

    [Fact]
    public void No_base_url_input_or_switch_button_is_rendered()
    {
        var voterClient = new FakeVoterClient
        {
            Files =
            [
                new LlmRouterModelFileStatusInfo(FileName: "model.onnx", true, 10, SyncedAtUtc: DateTimeOffset.UtcNow,
                    true, false)
            ]
        };
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();

        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Switch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_voter_progress_bars_render_only_while_syncing_and_the_lower_one_names_the_current_file()
    {
        var tcs = new TaskCompletionSource();
        var voterClient = new FakeVoterClient
        {
            Files = [new LlmRouterModelFileStatusInfo(FileName: "model.onnx", false, 0, null, false, false)],
            SyncEvents =
            [
                new LlmRouterModelSyncEvent(
                    Plan: new LlmRouterModelSyncPlanInfo(
                        Files: [new LlmRouterModelSyncPlanFileInfo(FileName: "model.onnx", 100)], 100),
                    null,
                    null),
                new LlmRouterModelSyncEvent(
                    null,
                    Progress: new LlmRouterModelSyncProgressInfo(FileName: "model.onnx",
                        Stage: LlmRouterModelSyncStageInfo.Downloading, 40, null, 100),
                    null)
            ],
            HoldBeforeFinalStatus = tcs.Task
        };
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);
        var cut = ctx.Render<BenchmarkData>();

        cut.FindAll("[role=progressbar]").Should().BeEmpty();

        // The Task Matrix panel's own "Update" button is disabled here (its corpus is Current), so
        // Last(), not First(), reaches the voter section's enabled one.
        await cut.InvokeAsync(() =>
            cut.FindAll("button")
                .Last(b => b.TextContent.Contains(value: "Update", comparisonType: StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().HaveCount(2));
        cut.Markup.Should().Contain("model.onnx");

        tcs.SetResult();

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().BeEmpty());
    }

    [Fact]
    public void The_voter_disclosure_starts_collapsed_and_reveals_every_file_when_toggled()
    {
        var files = Enumerable.Range(1, 5)
            .Select(i => new LlmRouterModelFileStatusInfo(FileName: $"file{i}.bin", true, 10,
                SyncedAtUtc: DateTimeOffset.UtcNow, true, false))
            .ToArray();
        var voterClient = new FakeVoterClient { Files = files };
        using var ctx = NewContext(client: new FakeClient(BenchmarkDataAdminState.Current), voterClient: voterClient);

        var cut = ctx.Render<BenchmarkData>();

        var toggle = cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Show 5 files", comparisonType: StringComparison.Ordinal));
        toggle.GetAttribute("aria-expanded").Should().Be("false");

        // Same collapsed-but-mounted contract as the Task Matrix disclosure above.
        var disclosure = cut.FindAll(".ls-disclosure")
            .Single(d => d.InnerHtml.Contains(value: "file1.bin", comparisonType: StringComparison.Ordinal));
        disclosure.ClassList.Should().NotContain("open");
        disclosure.HasAttribute("inert").Should().BeTrue();

        toggle.Click();

        cut.FindAll("button").First(b =>
                b.TextContent.Contains(value: "Hide files", comparisonType: StringComparison.Ordinal))
            .GetAttribute("aria-expanded").Should().Be("true");
        disclosure = cut.FindAll(".ls-disclosure")
            .Single(d => d.InnerHtml.Contains(value: "file1.bin", comparisonType: StringComparison.Ordinal));
        disclosure.ClassList.Should().Contain("open");
        disclosure.HasAttribute("inert").Should().BeFalse();
        foreach (var file in files) cut.Markup.Should().Contain(file.FileName);
    }

    [Fact]
    public void A_rejected_status_load_shows_the_rejection_rather_than_the_unreachable_state()
    {
        // The router answered - it just refused - so "Router unreachable" would both misstate the cause
        // and replace the panel that has to carry the actual reason.
        using var ctx = NewContext(new FakeClient
        {
            StatusError =
                new BenchmarkDataAdminException("Could not read the benchmark data status: database is locked.")
        });

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().NotContain("Router unreachable");
        cut.Markup.Should().Contain("database is locked");
    }

    [Fact]
    public void A_rejected_recheck_keeps_the_panel_on_screen()
    {
        var client = new FakeClient(state: BenchmarkDataAdminState.CheckFailed, Reason: "boom")
        {
            RecheckError = new BenchmarkDataAdminException("Could not recheck the benchmark data: boom")
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        // The per-card button is disabled in CheckFailed, so this now goes through Resync.
        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Resync", comparisonType: StringComparison.Ordinal)).Click();

        cut.Markup.Should().NotContain("Router unreachable");
        cut.Markup.Should().Contain("Could not recheck the benchmark data");
    }

    private sealed class FakeClient : IBenchmarkDataAdminClient
    {
        private readonly IReadOnlyList<BenchmarkFileStatusInfo> _files;
        private readonly BenchmarkDataAdminState _initialState;
        private readonly string? _reason;

        public FakeClient(BenchmarkDataAdminState state, params BenchmarkFileStatusInfo[] files)
            : this(state: state, null, files: files)
        {
        }

        public FakeClient(BenchmarkDataAdminState state, string? Reason, params BenchmarkFileStatusInfo[] files)
        {
            _initialState = state;
            _reason = Reason;
            _files = files;
        }

        public FakeClient()
        {
            _initialState = BenchmarkDataAdminState.Update;
            _files = [];
        }

        public BenchmarkDataAdminException? StatusError { get; init; }

        public BenchmarkDataAdminException? RecheckError { get; init; }

        public IReadOnlyList<BenchmarkSyncEvent> SyncEvents { get; init; } = [];

        /// <summary>
        /// When set, awaited after every queued <see cref="SyncEvents"/> entry has been yielded and before
        /// the async enumerable completes - so a test can assert on the store's mid-sync state (still
        /// <c>IsSyncing</c>, since the store's <c>finally</c> only runs once enumeration ends) and then
        /// let the sync finish by completing this task.
        /// </summary>
        public Task? HoldBeforeFinalStatus { get; init; }

        public int RecheckCount { get; private set; }

        public int SyncCount { get; private set; }

        public Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return StatusError is not null
                ? Task.FromException<BenchmarkDataStatusInfo>(StatusError)
                : Task.FromResult(new BenchmarkDataStatusInfo(State: _initialState, Reason: _reason,
                    CheckedAtUtc: DateTimeOffset.UtcNow, Files: _files));
        }

        public Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default)
        {
            RecheckCount++;
            return RecheckError is not null
                ? Task.FromException<BenchmarkDataStatusInfo>(RecheckError)
                : Task.FromResult(new BenchmarkDataStatusInfo(State: BenchmarkDataAdminState.Current, null,
                    CheckedAtUtc: DateTimeOffset.UtcNow, Files: _files));
        }

        public async IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SyncCount++;
            var events = SyncEvents.Count > 0
                ? SyncEvents
                :
                [
                    new BenchmarkSyncEvent(null, null,
                        FinalStatus: new BenchmarkDataStatusInfo(State: BenchmarkDataAdminState.Current, null,
                            CheckedAtUtc: DateTimeOffset.UtcNow, Files: _files))
                ];

            // Task.CompletedTask, not Task.Yield: this satisfies CS1998 without introducing a real
            // asynchronous gap, so the events publish synchronously within the test's Click() call rather
            // than racing bUnit's render against a threadpool continuation.
            await Task.CompletedTask;
            foreach (var e in events) yield return e;

            if (HoldBeforeFinalStatus is { } hold) await hold;
        }
    }

    /// <summary>
    /// A configurable, reachable-by-default <see cref="ILlmRouterModelAdminClient"/> fake, so
    /// <see cref="NewContext"/> can register a <see cref="LlmRouterModelStore"/> for the Local Voter Model
    /// section without needing a live proxy. Defaults to no files (a "Current" vacuously-true status) for
    /// tests that don't exercise this section; tests that do pass <see cref="Files"/> and/or
    /// <see cref="SyncEvents"/>.
    /// </summary>
    private sealed class FakeVoterClient : ILlmRouterModelAdminClient
    {
        public LlmRouterModelAdminException? StatusError { get; init; }

        public IReadOnlyList<LlmRouterModelFileStatusInfo> Files { get; init; } = [];

        public IReadOnlyList<LlmRouterModelSyncEvent> SyncEvents { get; init; } = [];

        /// <summary>Same purpose as <c>FakeClient.HoldBeforeFinalStatus</c>.</summary>
        public Task? HoldBeforeFinalStatus { get; init; }

        public int GetStatusCount { get; private set; }

        public int SyncCount { get; private set; }

        public Task<LlmRouterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            GetStatusCount++;
            return StatusError is not null
                ? Task.FromException<LlmRouterModelStatusInfo>(StatusError)
                : Task.FromResult(new LlmRouterModelStatusInfo(BaseUrl: string.Empty, Files: Files,
                    Current: Files.All(f => f.Synced)));
        }

        public Task<LlmRouterModelStatusInfo> SetBaseUrlAsync(string baseUrl,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmRouterModelStatusInfo(BaseUrl: baseUrl, Files: Files,
                Current: Files.All(f => f.Synced)));
        }

        public async IAsyncEnumerable<LlmRouterModelSyncEvent> SyncAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SyncCount++;
            var events = SyncEvents.Count > 0
                ? SyncEvents
                :
                [
                    new LlmRouterModelSyncEvent(null, null,
                        FinalStatus: new LlmRouterModelStatusInfo(BaseUrl: string.Empty, Files: Files, true))
                ];

            // Same synchronous-yield reasoning as FakeClient.SyncAsync.
            await Task.CompletedTask;
            foreach (var e in events) yield return e;

            if (HoldBeforeFinalStatus is { } hold) await hold;
        }
    }
}