using AwesomeAssertions;
using Bunit;
using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ClusterModelAdmin"/>: the Governance tab's Cluster Model panel. Driven through a
/// fake <see cref="IClusterModelAdminClient"/> so nothing here needs a live proxy or a gRPC channel.
/// </summary>
public sealed class ClusterModelAdminTests
{
    private static Bunit.BunitContext NewContext(IClusterModelAdminClient client)
    {
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton(new ClusterModelAdminStore(client));
        return ctx;
    }

    private static ClusterModelStatusInfo NoArtifact(int entriesSinceLastRetrain = 7) => new(
        ArtifactPresent: false,
        ChosenK: 0,
        TrainedAtUtc: null,
        TrainedFrom: string.Empty,
        Clusters: [],
        BootstrapTaskCount: 0,
        MemoryEntryCount: 0,
        EntriesSinceLastRetrain: entriesSinceLastRetrain,
        RetentionDays: 30,
        MaxTranscriptRows: 50_000,
        CurrentTranscriptRowCount: 42);

    private static ClusterModelStatusInfo WithArtifact() => new(
        ArtifactPresent: true,
        ChosenK: 2,
        TrainedAtUtc: new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        TrainedFrom: "bootstrap_tasks=20, memory_entries=5, samples=25",
        Clusters:
        [
            new ClusterInfo(10, "mostly bug_fixing: sql, migration"),
            new ClusterInfo(15, "mostly test_generation"),
        ],
        BootstrapTaskCount: 20,
        MemoryEntryCount: 5,
        EntriesSinceLastRetrain: 3,
        RetentionDays: 30,
        MaxTranscriptRows: 50_000,
        CurrentTranscriptRowCount: 8);

    [Fact]
    public void No_artifact_renders_the_train_button_and_no_cluster_when_untrained()
    {
        using var ctx = NewContext(new FakeClient(NoArtifact()));

        var cut = ctx.Render<ClusterModelAdmin>();

        cut.Markup.Should().Contain("No cluster model trained yet");
        cut.Markup.Should().Contain("Train");
        cut.Markup.Should().NotContain("Retrain");
    }

    [Fact]
    public void Trained_artifact_renders_every_cluster_with_its_size_and_name()
    {
        using var ctx = NewContext(new FakeClient(WithArtifact()));

        var cut = ctx.Render<ClusterModelAdmin>();

        cut.Markup.Should().Contain("2 clusters");
        cut.Markup.Should().Contain("mostly bug_fixing: sql, migration");
        cut.Markup.Should().Contain("mostly test_generation");
        cut.Markup.Should().Contain(">10<");
        cut.Markup.Should().Contain(">15<");
        cut.Markup.Should().Contain("Retrain");
    }

    [Fact]
    public void Renders_the_transcript_retention_context_regardless_of_artifact_presence()
    {
        using var ctx = NewContext(new FakeClient(NoArtifact()));

        var cut = ctx.Render<ClusterModelAdmin>();

        cut.Markup.Should().Contain("42 of 50000 rows");
        cut.Markup.Should().Contain("30 days");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(new FakeClient { Error = new ClusterModelAdminException("nope", isUnavailable: true) });

        var cut = ctx.Render<ClusterModelAdmin>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void Retry_reloads_after_the_router_becomes_reachable()
    {
        var client = new FakeClient { Error = new ClusterModelAdminException("nope", isUnavailable: true) };
        using var ctx = NewContext(client);
        var cut = ctx.Render<ClusterModelAdmin>();
        cut.Markup.Should().Contain("Router unreachable");

        client.Error = null;
        client.Status = NoArtifact();
        cut.Find("button").Click();

        cut.Markup.Should().Contain("No cluster model trained yet");
    }

    [Fact]
    public void Clicking_train_streams_progress_then_shows_the_outcome_message()
    {
        var client = new FakeClient(NoArtifact())
        {
            RetrainEvents =
            [
                new ClusterRetrainEvent(new ClusterRetrainBootstrapProgressInfo(5), Result: null),
                new ClusterRetrainEvent(
                    BootstrapProgress: null,
                    new ClusterRetrainResultInfo(ClusterRetrainResultKindInfo.Trained, "Trained 2 clusters.", WithArtifact())),
            ],
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<ClusterModelAdmin>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Train")).Click();

        cut.Markup.Should().Contain("Trained 2 clusters.");
        cut.Markup.Should().Contain("2 clusters");
    }

    [Fact]
    public async Task Retraining_renders_the_training_state_with_the_running_bootstrap_progress_count()
    {
        // The result event is gated so the test can observe the "Training…" state - which only exists
        // between the first bootstrap-progress event and the terminal result - before releasing it.
        var gate = new TaskCompletionSource<bool>();
        var client = new FakeClient(NoArtifact())
        {
            Gate = gate,
            RetrainEvents =
            [
                new ClusterRetrainEvent(new ClusterRetrainBootstrapProgressInfo(5), Result: null),
                new ClusterRetrainEvent(
                    BootstrapProgress: null,
                    new ClusterRetrainResultInfo(ClusterRetrainResultKindInfo.Trained, "Trained.", WithArtifact())),
            ],
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<ClusterModelAdmin>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Train")).Click();
        cut.WaitForState(() => cut.Markup.Contains("Training…"));

        cut.Markup.Should().Contain("Training…");
        cut.Markup.Should().Contain("5 so far");

        gate.SetResult(true);
        cut.WaitForState(() => cut.Markup.Contains("Trained."));
        await Task.CompletedTask;
    }

    private sealed class FakeClient(ClusterModelStatusInfo? status = null) : IClusterModelAdminClient
    {
        public ClusterModelStatusInfo? Status { get; set; } = status;

        public ClusterModelAdminException? Error { get; set; }

        public IReadOnlyList<ClusterRetrainEvent> RetrainEvents { get; set; } = [];

        /// <summary>
        /// Optional gate awaited immediately before yielding a <see cref="ClusterRetrainEvent.Result"/>
        /// event, so a test can observe the streaming-but-not-yet-final state before releasing it.
        /// </summary>
        public TaskCompletionSource<bool>? Gate { get; set; }

        public Task<ClusterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Error is not null
                ? Task.FromException<ClusterModelStatusInfo>(Error)
                : Task.FromResult(Status!);

        public async IAsyncEnumerable<ClusterRetrainEvent> RetrainAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Error is not null)
            {
                throw Error;
            }

            foreach (var retrainEvent in RetrainEvents)
            {
                if (retrainEvent.Result is not null && Gate is not null)
                {
                    await Gate.Task;
                }

                yield return retrainEvent;
            }
        }
    }
}
