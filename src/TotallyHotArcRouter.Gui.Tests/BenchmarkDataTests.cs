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
    public void Current_state_renders_a_disabled_button_and_a_card_per_synced_file()
    {
        var syncedAt = DateTimeOffset.UtcNow.AddHours(-1);
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Current,
            new BenchmarkFileStatusInfo("models.json", Synced: true, SizeBytes: 1_400, RowCount: 3, syncedAt)));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("models.json");
        cut.Markup.Should().Contain("3 rows");
        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Current", StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Update_state_renders_an_enabled_update_button()
    {
        using var ctx = NewContext(new FakeClient(BenchmarkDataAdminState.Update,
            new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null)));

        var cut = ctx.Render<BenchmarkData>();

        cut.Markup.Should().Contain("Never synced");
        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal));
        button.HasAttribute("disabled").Should().BeFalse();
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
        cut.Markup.Should().Contain("Current");
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
                    new BenchmarkSyncProgressInfo("models.json", BenchmarkSyncStageInfo.Failed, null, null, "checksum mismatch"),
                    FinalStatus: null),
                new BenchmarkSyncEvent(Progress: null, FinalStatus: new BenchmarkDataStatusInfo(
                    BenchmarkDataAdminState.Update, null, DateTimeOffset.UtcNow,
                    [new BenchmarkFileStatusInfo("models.json", Synced: false, SizeBytes: 0, RowCount: 0, SyncedAtUtc: null)])),
            ],
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<BenchmarkData>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Update", StringComparison.Ordinal)).Click();

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
                : [new BenchmarkSyncEvent(Progress: null, FinalStatus: new BenchmarkDataStatusInfo(BenchmarkDataAdminState.Current, null, DateTimeOffset.UtcNow, _files))];

            // Task.CompletedTask, not Task.Yield: this satisfies CS1998 without introducing a real
            // asynchronous gap, so the events publish synchronously within the test's Click() call rather
            // than racing bUnit's render against a threadpool continuation.
            await Task.CompletedTask;
            foreach (var e in events)
            {
                yield return e;
            }
        }
    }

    /// <summary>
    /// A minimal, always-reachable, no-files <see cref="ILlmRouterModelAdminClient"/> fake, so
    /// <see cref="NewContext"/> can register a <see cref="LlmRouterModelStore"/> for the Local Voter Model
    /// section without needing a live proxy. None of the tests in this file exercise that section, so its
    /// behavior does not need to vary.
    /// </summary>
    private sealed class FakeVoterClient : ILlmRouterModelAdminClient
    {
        public LlmRouterModelAdminException? StatusError { get; init; }

        public int GetStatusCount { get; private set; }

        public Task<LlmRouterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            GetStatusCount++;
            return StatusError is not null
                ? Task.FromException<LlmRouterModelStatusInfo>(StatusError)
                : Task.FromResult(new LlmRouterModelStatusInfo(string.Empty, [], Current: true));
        }

        public Task<LlmRouterModelStatusInfo> SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmRouterModelStatusInfo(baseUrl, [], Current: true));

        public async IAsyncEnumerable<LlmRouterModelSyncEvent> SyncAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new LlmRouterModelSyncEvent(Progress: null, FinalStatus: new LlmRouterModelStatusInfo(string.Empty, [], Current: true));
        }
    }
}
