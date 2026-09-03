using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="RouterModelAdmin"/>: the Governance tab's Router Model panel. Driven through a
/// fake <see cref="ILogRegModelAdminClient"/> so nothing here needs a live proxy or a gRPC channel.
/// </summary>
public sealed class RouterModelAdminTests
{
    private static BunitContext NewContext(ILogRegModelAdminClient client)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new LogRegModelAdminStore(client));
        return ctx;
    }

    private static LogRegModelStatusInfo NoArtifact(int entriesSinceLastRetrain = 7)
    {
        return new LogRegModelStatusInfo(
            false,
            0,
            null,
            TrainedFrom: string.Empty,
            0,
            0,
            0,
            EntriesSinceLastRetrain: entriesSinceLastRetrain,
            500,
            3.0);
    }

    private static LogRegModelStatusInfo WithArtifact()
    {
        return new LogRegModelStatusInfo(
            true,
            1024,
            TrainedAtUtc: new DateTimeOffset(2026, 7, 16, 12, 0, 0, offset: TimeSpan.Zero),
            TrainedFrom: "bootstrap_tasks=20, memory_entries=5, samples=25",
            20,
            5,
            8,
            3,
            500,
            3.0);
    }

    [Fact]
    public void No_artifact_renders_the_train_button_and_no_model_when_untrained()
    {
        using var ctx = NewContext(new FakeClient(NoArtifact()));

        var cut = ctx.Render<RouterModelAdmin>();

        cut.Markup.Should().Contain("No logreg model trained yet");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Train");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Retrain"));
    }

    [Fact]
    public void Trained_artifact_renders_model_count_and_provenance()
    {
        using var ctx = NewContext(new FakeClient(WithArtifact()));

        var cut = ctx.Render<RouterModelAdmin>();

        cut.Markup.Should().Contain("8 models");
        cut.Markup.Should().Contain("bootstrap_tasks=20, memory_entries=5, samples=25");
        cut.Markup.Should().Contain("1024-dimensional embedding");
        cut.Markup.Should().Contain("Retrain");
    }

    [Fact]
    public void Renders_the_retrain_configuration_context_regardless_of_artifact_presence()
    {
        using var ctx = NewContext(new FakeClient(NoArtifact()));

        var cut = ctx.Render<RouterModelAdmin>();

        cut.Markup.Should().Contain("every 500 new live entries");
        cut.Markup.Should().Contain("3.0");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(new FakeClient
            { Error = new LogRegModelAdminException(message: "nope", isUnavailable: true) });

        var cut = ctx.Render<RouterModelAdmin>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void Retry_reloads_after_the_router_becomes_reachable()
    {
        var client = new FakeClient { Error = new LogRegModelAdminException(message: "nope", isUnavailable: true) };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RouterModelAdmin>();
        cut.Markup.Should().Contain("Router unreachable");

        client.Error = null;
        client.Status = NoArtifact();
        cut.Find("button").Click();

        cut.Markup.Should().Contain("No logreg model trained yet");
    }

    [Fact]
    public void Clicking_train_streams_progress_then_shows_the_outcome_message()
    {
        var client = new FakeClient(NoArtifact())
        {
            RetrainEvents =
            [
                new LogRegRetrainEvent(BootstrapProgress: new LogRegRetrainBootstrapProgressInfo(5), null),
                new LogRegRetrainEvent(
                    null,
                    Result: new LogRegRetrainResultInfo(Kind: LogRegRetrainResultKindInfo.Trained,
                        Message: "Trained 8 heads.", Status: WithArtifact()))
            ]
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RouterModelAdmin>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Train")).Click();

        cut.Markup.Should().Contain("Trained 8 heads.");
        cut.Markup.Should().Contain("8 models");
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
                new LogRegRetrainEvent(BootstrapProgress: new LogRegRetrainBootstrapProgressInfo(5), null),
                new LogRegRetrainEvent(
                    null,
                    Result: new LogRegRetrainResultInfo(Kind: LogRegRetrainResultKindInfo.Trained, Message: "Trained.",
                        Status: WithArtifact()))
            ]
        };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RouterModelAdmin>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Train")).Click();
        cut.WaitForState(() => cut.Markup.Contains("Training…"));

        cut.Markup.Should().Contain("Training…");
        cut.Markup.Should().Contain("5 so far");

        gate.SetResult(true);
        cut.WaitForState(() => cut.Markup.Contains("Trained."));
        await Task.CompletedTask;
    }

    private sealed class FakeClient(LogRegModelStatusInfo? status = null) : ILogRegModelAdminClient
    {
        public LogRegModelStatusInfo? Status { get; set; } = status;

        public LogRegModelAdminException? Error { get; set; }

        public IReadOnlyList<LogRegRetrainEvent> RetrainEvents { get; set; } = [];

        /// <summary>
        /// Optional gate awaited immediately before yielding a <see cref="LogRegRetrainEvent.Result"/>
        /// event, so a test can observe the streaming-but-not-yet-final state before releasing it.
        /// </summary>
        public TaskCompletionSource<bool>? Gate { get; set; }

        public Task<LogRegModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Error is not null
                ? Task.FromException<LogRegModelStatusInfo>(Error)
                : Task.FromResult(Status!);
        }

        public async IAsyncEnumerable<LogRegRetrainEvent> RetrainAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Error is not null) throw Error;

            foreach (var retrainEvent in RetrainEvents)
            {
                if (retrainEvent.Result is not null && Gate is not null) await Gate.Task;

                yield return retrainEvent;
            }
        }
    }
}