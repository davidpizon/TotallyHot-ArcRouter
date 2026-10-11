using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TestContext = Xunit.TestContext;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ConversationExportStore"/>: the summary load that swallows a failure into the
/// reachability state, and the export that tracks progress and result and rethrows a failure so the dialog can
/// show it (#165 phase 3).
/// </summary>
public sealed class ConversationExportStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Constructor_RejectsANullClient()
    {
        var act = () => new ConversationExportStore((IConversationAdminClient)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadSummaryAsync_HoldsTheRoutersSummary()
    {
        var store = new ConversationExportStore(new FakeConversationAdminClient());

        var loaded = await store.LoadSummaryAsync(Ct);

        loaded.Should().BeTrue();
        store.Summary.Should().Be(new ConversationExportSummaryInfo(3, 1_250, 5_242_880));
        store.IsReachable.Should().BeTrue();
        store.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public async Task LoadSummaryAsync_AnUnreachableRouterIsSwallowedIntoTheReachabilityState()
    {
        var client = new FakeConversationAdminClient
        {
            SummaryFailure = new GrpcAdminException("Could not read the conversation store: the router is not reachable.", isUnavailable: true),
        };
        var store = new ConversationExportStore(client);

        var loaded = await store.LoadSummaryAsync(Ct);

        loaded.Should().BeFalse();
        store.Summary.Should().BeNull();
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().Contain("not reachable");
    }

    [Fact]
    public async Task ExportAsync_ReportsProgressAndHoldsTheResult()
    {
        var client = new FakeConversationAdminClient
        {
            Progress = [new(1, 100), new(2, 250)],
        };
        var store = new ConversationExportStore(client);
        var seen = new List<ConversationExportProgressInfo?>();
        store.Changed += () => seen.Add(store.Progress);
        var filter = new ConversationExportFilterInfo(Harness: "codex");

        var result = await store.ExportAsync(filter, @"C:\exports\a.zip", "authz", Ct);

        result.Path.Should().Be(@"C:\exports\a.zip");
        store.Result.Should().Be(result);
        store.Progress.Should().Be(new ConversationExportProgressInfo(2, 250));
        store.IsExporting.Should().BeFalse();
        seen.Should().Contain(new ConversationExportProgressInfo(1, 100));
        seen.Should().Contain(new ConversationExportProgressInfo(2, 250));
        client.Filters.Should().Equal(filter);
        client.Destinations.Should().Equal(@"C:\exports\a.zip");
        client.Authorizations.Should().Equal("authz");
        store.IsReachable.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_IsExportingWhileTheStreamIsOpen()
    {
        var hold = new TaskCompletionSource();
        var client = new FakeConversationAdminClient { HoldBeforeResult = hold.Task };
        var store = new ConversationExportStore(client);

        var running = store.ExportAsync(new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz", Ct);

        store.IsExporting.Should().BeTrue();
        hold.SetResult();
        await running;
        store.IsExporting.Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_ARefusalIsRethrown_AndLeavesNoResult()
    {
        var client = new FakeConversationAdminClient
        {
            ExportFailure = new GrpcAdminException("The conversation export failed: Passkey verification is required for this operation."),
        };
        var store = new ConversationExportStore(client);

        var act = () => store.ExportAsync(new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz", Ct);

        (await act.Should().ThrowAsync<GrpcAdminException>()).Which.Message.Should().Contain("Passkey verification is required");
        store.Result.Should().BeNull();
        store.IsExporting.Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_AnUnreachableRouterMarksTheStoreUnreachable()
    {
        var client = new FakeConversationAdminClient
        {
            ExportFailure = new GrpcAdminException("The conversation export failed: the router is not reachable.", isUnavailable: true),
        };
        var store = new ConversationExportStore(client);

        await Assert.ThrowsAsync<GrpcAdminException>(
            () => store.ExportAsync(new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz", Ct));

        store.IsReachable.Should().BeFalse();
        store.LastError.Should().Contain("not reachable");
    }

    [Fact]
    public async Task Reset_ClearsTheLastExport()
    {
        var store = new ConversationExportStore(new FakeConversationAdminClient());
        await store.ExportAsync(new ConversationExportFilterInfo(), @"C:\exports\a.zip", "authz", Ct);

        store.Reset();

        store.Result.Should().BeNull();
        store.Progress.Should().BeNull();
    }

    [Fact]
    public async Task ExportAsync_RejectsMissingArguments()
    {
        var store = new ConversationExportStore(new FakeConversationAdminClient());

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ExportAsync(null!, @"C:\a.zip", "authz", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExportAsync(new ConversationExportFilterInfo(), "", "authz", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExportAsync(new ConversationExportFilterInfo(), @"C:\a.zip", "", Ct));
    }
}
