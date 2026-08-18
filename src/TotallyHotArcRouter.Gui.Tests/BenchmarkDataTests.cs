using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="BenchmarkData"/>: the Governance tab's Benchmark Data panel. Driven through a fake
/// <see cref="IBenchmarkDataAdminClient"/> so nothing here needs a live proxy or a gRPC channel, mirroring
/// <c>PriceSourcesAdminTests</c>.
/// </summary>
public sealed class BenchmarkDataTests
{
    private static Bunit.BunitContext NewContext(IBenchmarkDataAdminClient client) =>
        NewContext(client, new FakeVoterClient());

    private static Bunit.BunitContext NewContext(IBenchmarkDataAdminClient client, ILlmRouterModelAdminClient voterClient)
    {
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton(new BenchmarkDataStore(client));
        ctx.Services.AddSingleton(new LlmRouterModelStore(voterClient));
        return ctx;
    }

    [Fact]
    public void Section_header_reads_task_matrix()
    {
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo("models.json", Synced: true, SizeBytes: 1_400, RowCount: 3, DateTimeOffset.UtcNow)));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Task Matrix");
        cut.Markup.Should().NotContain("CodeRouterBench Corpus");
    }

    [Fact]
    public void Exactly_one_task_matrix_card_renders_regardless_of_file_count()
    {
        var files = Enumerable.Range(1, 8)
            .Select(i => new BenchmarkFileStatusInfo($"file{i}.json", Synced: true, SizeBytes: 10, RowCount: 1, DateTimeOffset.UtcNow))
            .ToArray();
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current, files));

        var cut = ctx.Render<BenchmarkData>();

        // One Task Matrix card regardless of how many corpus files it lists, plus one Local Voter Model
        // card from the always-present voter section (see NewContext's default FakeVoterClient).
        cut.FindAll(".ds-surface-card-draggable").Should().HaveCount(2);
    }

    [Fact]
    public void Current_state_renders_an_enabled_reverify_button_and_the_file_is_hidden_behind_the_disclosure()
    {
        var syncedAt = DateTimeOffset.UtcNow.AddHours(-1);
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo("models.json", Synced: true, SizeBytes: 1_400, RowCount: 3, syncedAt)));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().NotContain("models.json");
        // Mirrors the Local Voter Model panel's "Current" state: the button stays clickable so the
        // operator can re-verify the corpus against Hugging Face rather than trusting the ledger forever.
        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Re-verify", StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeFalse();

        cut.FindAll("button").First(b => b.TextContent.Contains("Show", StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("models.json");
        cut.Markup.Should().Contain("3 rows");
    }

    [Fact]
    public void Update_state_renders_an_enabled_update_button()
    {
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null)));

        var cut = ctx.Render<BenchmarkData>();
        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeFalse();

        cut.FindAll("button").First(b => b.TextContent.Contains("Show", StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("Never synced");
    }

    [Fact]
    public void The_disclosure_starts_collapsed_and_reveals_every_file_when_toggled()
    {
        var files = Enumerable.Range(1, 8)
            .Select(i => new BenchmarkFileStatusInfo($"file{i}.json", Synced: true, SizeBytes: 10, RowCount: 1, DateTimeOffset.UtcNow))
            .ToArray();
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current, files));

        var cut = ctx.Render<BenchmarkData>();

        var toggle = cut.FindAll("button").First(b => b.TextContent.Contains("Show 8 files", StringComparison.Ordinal));
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        foreach (var file in files)
        {
            cut.Markup.Should().NotContain(file.FileName);
        }

        toggle.Click();

        cut.FindAll("button").First(b => b.TextContent.Contains("Hide files", StringComparison.Ordinal))
            .GetAttribute("aria-expanded").Should().Be("true");
        foreach (var file in files)
        {
            cut.Markup.Should().Contain(file.FileName);
        }
    }

    [Fact]
    public async Task Two_progress_bars_render_only_while_syncing_and_the_lower_one_names_the_current_file()
    {
        var tcs = new TaskCompletionSource();
        var client = new FakeClient(BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null))
        {
            SyncEvents =
            [
                new BenchmarkSyncEvent(
                    Plan: new BenchmarkSyncPlanInfo([new BenchmarkSyncPlanFileInfo("models.json", 100)], TotalBytes: 100),
                    Progress: null,
                    FinalStatus: null),
                new BenchmarkSyncEvent(
                    Plan: null,
                    Progress: new BenchmarkSyncProgressInfo("models.json", BenchmarkSyncStageInfo.Downloading, 40, null, null, TotalBytes: 100),
                    FinalStatus: null),
            ],
            HoldBeforeFinalStatus = tcs.Task,
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<BenchmarkData>();

        cut.FindAll("[role=progressbar]").Should().BeEmpty();

        // Click() only dispatches the event; RunAction's async continuation (parked on
        // HoldBeforeFinalStatus below) keeps running after Click() returns, so the two progress bars
        // and their later disappearance are asserted via WaitForAssertion rather than by awaiting Click().
        await cut.InvokeAsync(() =>
            cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().HaveCount(2));
        cut.Markup.Should().Contain("models.json");

        tcs.SetResult();

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().BeEmpty());
    }

    [Fact]
    public void CheckFailed_state_renders_the_reason_and_an_enabled_button()
    {
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.CheckFailed, Reason: "failed to connect"));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Check Failed");
        cut.Markup.Should().Contain("failed to connect");
        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Check Failed", StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Clicking_update_runs_a_sync_and_becomes_current()
    {
        var client = new FakeClient(BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null));
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal)).Click();

        client.SyncCount.Should().Be(1);
        // Once current, the button relabels to "Re-verify" (still enabled) rather than a static "Current".
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Re-verify", StringComparison.Ordinal));
    }

    [Fact]
    public void Clicking_reverify_on_a_current_corpus_runs_a_sync_rather_than_a_recheck()
    {
        var client = new FakeClient(BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo("models.json", Synced: true, SizeBytes: 1_400, RowCount: 3, DateTimeOffset.UtcNow));
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Re-verify", StringComparison.Ordinal)).Click();

        client.SyncCount.Should().Be(1);
        client.RecheckCount.Should().Be(0);
    }

    [Fact]
    public void Clicking_check_failed_rechecks_rather_than_syncing_blind()
    {
        var client = new FakeClient(BenchmarkDataAdminState.CheckFailed, Reason: "boom");
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Check Failed", StringComparison.Ordinal)).Click();

        client.RecheckCount.Should().Be(1);
        client.SyncCount.Should().Be(0);
    }

    [Fact]
    public void A_failed_files_error_is_rendered_on_its_card()
    {
        var client = new FakeClient(BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null))
        {
            SyncEvents =
            [
                new BenchmarkSyncEvent(
                    Plan: null,
                    Progress: new BenchmarkSyncProgressInfo("models.json", BenchmarkSyncStageInfo.Failed, null, null, "checksum mismatch"),
                    FinalStatus: null),
                new BenchmarkSyncEvent(Plan: null, Progress: null, FinalStatus: new BenchmarkDataStatusInfo(
                    BenchmarkDataAdminState.Update, null, DateTimeOffset.UtcNow,
                    [new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null)])),
            ],
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Show", StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("checksum mismatch");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_is_down()
    {
        using var ctx = NewContext(new FakeClient
        {
            StatusError = new BenchmarkDataAdminException("the router is not reachable.", isUnavailable: true),
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
            StatusError = new BenchmarkDataAdminException("the router is not reachable.", isUnavailable: true),
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
        using var store = new BenchmarkDataStore(logger: null, serverAddress: "https://localhost:65111");

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
            StatusError = new LlmRouterModelAdminException("the router is not reachable.", isUnavailable: true),
        };
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current), voterClient);

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Could not reach the router.");

        cut.FindAll("button").First(b => b.TextContent.Contains("Retry", StringComparison.Ordinal)).Click();

        voterClient.GetStatusCount.Should().Be(2);
    }

    [Fact]
    public void Voter_current_state_renders_an_enabled_reverify_button_that_reruns_sync()
    {
        // Unlike the corpus panel, the voter's "Current" state must stay clickable: checksum verification
        // is in-memory only and doesn't survive a process restart, so re-running the sync is the only way
        // to confirm already-cached files are still intact.
        var voterClient = new FakeVoterClient();
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current), voterClient);

        var cut = ctx.Render<BenchmarkData>();

        // The Task Matrix panel's own button also reads "Re-verify" when its corpus is current, and it
        // renders first in the DOM - the voter section's button is the last one.
        var button = cut.FindAll("button").Last(b => b.TextContent.Contains("Re-verify", StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeFalse();

        button.Click();

        voterClient.SyncCount.Should().Be(1);
    }

    [Fact]
    public void Exactly_one_voter_card_renders_regardless_of_file_count()
    {
        var files = Enumerable.Range(1, 5)
            .Select(i => new LlmRouterModelFileStatusInfo($"file{i}.bin", Synced: true, SizeBytes: 10, DateTimeOffset.UtcNow, ChecksumVerified: true, IsOptional: false))
            .ToArray();
        var voterClient = new FakeVoterClient { Files = files };
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current), voterClient);

        var cut = ctx.Render<BenchmarkData>();

        // One card for the Task Matrix section (no files there) plus one for Local Voter Model.
        cut.FindAll(".ds-surface-card-draggable").Should().HaveCount(2);
    }

    [Fact]
    public void No_base_url_input_or_switch_button_is_rendered()
    {
        var voterClient = new FakeVoterClient
        {
            Files = [new LlmRouterModelFileStatusInfo("model.onnx", Synced: true, SizeBytes: 10, DateTimeOffset.UtcNow, ChecksumVerified: true, IsOptional: false)],
        };
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current), voterClient);

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
            Files = [new LlmRouterModelFileStatusInfo("model.onnx", Synced: false, SizeBytes: 0, SyncedAtUtc: null, ChecksumVerified: false, IsOptional: false)],
            SyncEvents =
            [
                new LlmRouterModelSyncEvent(
                    Plan: new LlmRouterModelSyncPlanInfo([new LlmRouterModelSyncPlanFileInfo("model.onnx", 100)], TotalBytes: 100),
                    Progress: null,
                    FinalStatus: null),
                new LlmRouterModelSyncEvent(
                    Plan: null,
                    Progress: new LlmRouterModelSyncProgressInfo("model.onnx", LlmRouterModelSyncStageInfo.Downloading, 40, null, TotalBytes: 100),
                    FinalStatus: null),
            ],
            HoldBeforeFinalStatus = tcs.Task,
        };
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current), voterClient);
        var cut = ctx.Render<BenchmarkData>();

        cut.FindAll("[role=progressbar]").Should().BeEmpty();

        await cut.InvokeAsync(() =>
            cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().HaveCount(2));
        cut.Markup.Should().Contain("model.onnx");

        tcs.SetResult();

        cut.WaitForAssertion(() => cut.FindAll("[role=progressbar]").Should().BeEmpty());
    }

    [Fact]
    public void The_voter_disclosure_starts_collapsed_and_reveals_every_file_when_toggled()
    {
        var files = Enumerable.Range(1, 5)
            .Select(i => new LlmRouterModelFileStatusInfo($"file{i}.bin", Synced: true, SizeBytes: 10, DateTimeOffset.UtcNow, ChecksumVerified: true, IsOptional: false))
            .ToArray();
        var voterClient = new FakeVoterClient { Files = files };
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current), voterClient);

        var cut = ctx.Render<BenchmarkData>();

        var toggle = cut.FindAll("button").First(b => b.TextContent.Contains("Show 5 files", StringComparison.Ordinal));
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        foreach (var file in files)
        {
            cut.Markup.Should().NotContain(file.FileName);
        }

        toggle.Click();

        cut.FindAll("button").First(b => b.TextContent.Contains("Hide files", StringComparison.Ordinal))
            .GetAttribute("aria-expanded").Should().Be("true");
        foreach (var file in files)
        {
            cut.Markup.Should().Contain(file.FileName);
        }
    }

    [Fact]
    public void A_rejected_status_load_shows_the_rejection_rather_than_the_unreachable_state()
    {
        // The router answered - it just refused - so "Router unreachable" would both misstate the cause
        // and replace the panel that has to carry the actual reason.
        using var ctx = NewContext(new FakeClient
        {
            StatusError = new BenchmarkDataAdminException("Could not read the benchmark data status: database is locked."),
        });

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().NotContain("Router unreachable");
        cut.Markup.Should().Contain("database is locked");
    }

    [Fact]
    public void A_rejected_recheck_keeps_the_panel_on_screen()
    {
        var client = new FakeClient(BenchmarkDataAdminState.CheckFailed, Reason: "boom")
        {
            RecheckError = new BenchmarkDataAdminException("Could not recheck the benchmark data: boom"),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Check Failed", StringComparison.Ordinal)).Click();

        cut.Markup.Should().NotContain("Router unreachable");
        cut.Markup.Should().Contain("Could not recheck the benchmark data");
    }

    private sealed class FakeClient : IBenchmarkDataAdminClient
    {
        private readonly BenchmarkDataAdminState _initialState;
        private readonly string? _reason;
        private readonly IReadOnlyList<BenchmarkFileStatusInfo> _files;

        public FakeClient(BenchmarkDataAdminState state, params BenchmarkFileStatusInfo[] files)
            : this(state, Reason: null, files)
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

        public Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default) =>
            StatusError is not null
                ? Task.FromException<BenchmarkDataStatusInfo>(StatusError)
                : Task.FromResult(new BenchmarkDataStatusInfo(_initialState, _reason, DateTimeOffset.UtcNow, _files));

        public Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default)
        {
            RecheckCount++;
            return RecheckError is not null
                ? Task.FromException<BenchmarkDataStatusInfo>(RecheckError)
                : Task.FromResult(new BenchmarkDataStatusInfo(BenchmarkDataAdminState.Current, null, DateTimeOffset.UtcNow, _files));
        }

        public async IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SyncCount++;
            var events = SyncEvents.Count > 0
                ? SyncEvents
                : [new BenchmarkSyncEvent(Plan: null, Progress: null, FinalStatus: new BenchmarkDataStatusInfo(BenchmarkDataAdminState.Current, null, DateTimeOffset.UtcNow, _files))];

            // Task.CompletedTask, not Task.Yield: this satisfies CS1998 without introducing a real
            // asynchronous gap, so the events publish synchronously within the test's Click() call rather
            // than racing bUnit's render against a threadpool continuation.
            await Task.CompletedTask;
            foreach (var e in events)
            {
                yield return e;
            }

            if (HoldBeforeFinalStatus is { } hold)
            {
                await hold;
            }
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
                : Task.FromResult(new LlmRouterModelStatusInfo(string.Empty, Files, Current: Files.All(f => f.Synced)));
        }

        public Task<LlmRouterModelStatusInfo> SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmRouterModelStatusInfo(baseUrl, Files, Current: Files.All(f => f.Synced)));

        public async IAsyncEnumerable<LlmRouterModelSyncEvent> SyncAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SyncCount++;
            var events = SyncEvents.Count > 0
                ? SyncEvents
                : [new LlmRouterModelSyncEvent(Plan: null, Progress: null, FinalStatus: new LlmRouterModelStatusInfo(string.Empty, Files, Current: true))];

            // Same synchronous-yield reasoning as FakeClient.SyncAsync.
            await Task.CompletedTask;
            foreach (var e in events)
            {
                yield return e;
            }

            if (HoldBeforeFinalStatus is { } hold)
            {
                await hold;
            }
        }
    }
}
