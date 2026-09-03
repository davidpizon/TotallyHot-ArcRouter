using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="LogRegModelAdminClient"/> - the wire-to-view mapping and error translation
/// behind the Governance → Router Model panel, mirroring <c>ClusterModelAdminClientTests</c>.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, same reasoning as
/// <c>ClusterModelAdminClientTests</c>: the generated client exposes a protected parameterless
/// constructor precisely for test doubles.
/// </remarks>
public class LogRegModelAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_no_artifact_maps_artifact_present_false()
    {
        var stub = new StubClient
        {
            StatusResponse = new Contract.LogRegModelStatusResponse
            {
                ArtifactPresent = false,
                EntriesSinceLastRetrain = 42,
                RetrainThreshold = 500,
                LiveSampleWeight = 3.0
            }
        };
        using var client = new LogRegModelAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ArtifactPresent.Should().BeFalse();
        status.EmbeddingDimension.Should().Be(0);
        status.TrainedAtUtc.Should().BeNull();
        status.EntriesSinceLastRetrain.Should().Be(42);
        status.RetrainThreshold.Should().Be(500);
        status.LiveSampleWeight.Should().Be(3.0);
    }

    [Fact]
    public async Task GetStatusAsync_with_artifact_maps_provenance_and_trained_at()
    {
        var trainedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, offset: TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.LogRegModelStatusResponse
            {
                ArtifactPresent = true,
                EmbeddingDimension = 1024,
                TrainedAtUtc = Timestamp.FromDateTimeOffset(trainedAt),
                TrainedFrom = "bootstrap_tasks=20, memory_entries=5",
                BootstrapTaskCount = 20,
                MemoryEntryCount = 5,
                ModelsRepresented = 8,
                EntriesSinceLastRetrain = 3,
                RetrainThreshold = 500,
                LiveSampleWeight = 3.0
            }
        };
        using var client = new LogRegModelAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ArtifactPresent.Should().BeTrue();
        status.EmbeddingDimension.Should().Be(1024);
        status.TrainedAtUtc.Should().Be(trainedAt);
        status.TrainedFrom.Should().Be("bootstrap_tasks=20, memory_entries=5");
        status.ModelsRepresented.Should().Be(8);
    }

    [Fact]
    public async Task RetrainAsync_streams_bootstrap_progress_then_the_result()
    {
        var stub = new StubClient
        {
            RetrainEvents =
            [
                new Contract.LogRegRetrainStreamEvent
                {
                    BootstrapProgress = new Contract.LogRegRetrainBootstrapProgress { TasksEmbedded = 5 }
                },
                new Contract.LogRegRetrainStreamEvent
                {
                    BootstrapProgress = new Contract.LogRegRetrainBootstrapProgress { TasksEmbedded = 10 }
                },
                new Contract.LogRegRetrainStreamEvent
                {
                    Result = new Contract.LogRegRetrainResult
                    {
                        Kind = Contract.LogRegRetrainResultKind.Trained,
                        Message = "Trained 8 heads.",
                        Status = new Contract.LogRegModelStatusResponse
                            { ArtifactPresent = true, ModelsRepresented = 8 }
                    }
                }
            ]
        };
        using var client = new LogRegModelAdminClient(stub);

        var events = new List<LogRegRetrainEvent>();
        await foreach (var e in client.RetrainAsync(TestContext.Current.CancellationToken)) events.Add(e);

        events.Should().HaveCount(3);
        events[0].BootstrapProgress!.TasksEmbedded.Should().Be(5);
        events[0].Result.Should().BeNull();
        events[1].BootstrapProgress!.TasksEmbedded.Should().Be(10);

        events[2].BootstrapProgress.Should().BeNull();
        events[2].Result!.Kind.Should().Be(LogRegRetrainResultKindInfo.Trained);
        events[2].Result!.Message.Should().Be("Trained 8 heads.");
        events[2].Result!.Status.ModelsRepresented.Should().Be(8);
    }

    [Theory]
    [InlineData(Contract.LogRegRetrainResultKind.Trained, LogRegRetrainResultKindInfo.Trained)]
    [InlineData(Contract.LogRegRetrainResultKind.Declined, LogRegRetrainResultKindInfo.Declined)]
    [InlineData(Contract.LogRegRetrainResultKind.AlreadyRunning, LogRegRetrainResultKindInfo.AlreadyRunning)]
    // Unspecified stands in for a value this build cannot map. It must not read as Trained: the panel
    // would otherwise report success for an outcome nobody actually asserted.
    [InlineData(Contract.LogRegRetrainResultKind.Unspecified, LogRegRetrainResultKindInfo.Declined)]
    public async Task RetrainAsync_maps_result_kinds(
        Contract.LogRegRetrainResultKind wireKind,
        LogRegRetrainResultKindInfo expected)
    {
        var stub = new StubClient
        {
            RetrainEvents =
            [
                new Contract.LogRegRetrainStreamEvent
                {
                    Result = new Contract.LogRegRetrainResult
                        { Kind = wireKind, Message = "m", Status = new Contract.LogRegModelStatusResponse() }
                }
            ]
        };
        using var client = new LogRegModelAdminClient(stub);

        var events = new List<LogRegRetrainEvent>();
        await foreach (var e in client.RetrainAsync(TestContext.Current.CancellationToken)) events.Add(e);

        events.Single().Result!.Kind.Should().Be(expected);
    }

    [Fact]
    public async Task RetrainAsync_an_empty_oneof_maps_to_an_all_null_event_without_throwing()
    {
        var stub = new StubClient { RetrainEvents = [new Contract.LogRegRetrainStreamEvent()] };
        using var client = new LogRegModelAdminClient(stub);

        var events = new List<LogRegRetrainEvent>();
        await foreach (var e in client.RetrainAsync(TestContext.Current.CancellationToken)) events.Add(e);

        var single = events.Single();
        single.BootstrapProgress.Should().BeNull();
        single.Result.Should().BeNull();
    }

    [Fact]
    public async Task RetrainAsync_wraps_a_mid_stream_failure()
    {
        var stub = new StubClient
            { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new LogRegModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<LogRegModelAdminException>(async () =>
        {
            await foreach (var _ in client.RetrainAsync(TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Message.Should().Be("Logreg model retrain failed: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient
            { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new LogRegModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<LogRegModelAdminException>(() =>
            client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the logreg model status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
            { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        using var client = new LogRegModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<LogRegModelAdminException>(() =>
            client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the logreg model status: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new LogRegModelAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new LogRegModelAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LogRegModelAdminClient((Contract.RouterModelAdminService.RouterModelAdminServiceClient)null!));
    }

    /// <summary>An <see cref="IAsyncStreamReader{T}"/> fake that yields a fixed, in-memory sequence of messages.</summary>
    private sealed class FakeStreamReader<T>(IReadOnlyList<T> messages) : IAsyncStreamReader<T>
    {
        private int _index = -1;

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            if (_index >= messages.Count) return Task.FromResult(false);

            Current = messages[_index];
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.RouterModelAdminService.RouterModelAdminServiceClient
    {
        public Contract.LogRegModelStatusResponse StatusResponse { get; init; } = new();

        public IReadOnlyList<Contract.LogRegRetrainStreamEvent> RetrainEvents { get; init; } = [];

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.LogRegModelStatusResponse> GetLogRegModelStatusAsync(
            Contract.GetLogRegModelStatusRequest request,
            CallOptions options)
        {
            return Call(StatusResponse);
        }

        public override AsyncServerStreamingCall<Contract.LogRegRetrainStreamEvent> RetrainLogRegModel(
            Contract.RetrainLogRegModelRequest request,
            CallOptions options)
        {
            IAsyncStreamReader<Contract.LogRegRetrainStreamEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.LogRegRetrainStreamEvent>(RetrainEvents)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.LogRegRetrainStreamEvent>(
                responseStream: reader,
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        private AsyncUnaryCall<T> Call<T>(T response)
        {
            return new AsyncUnaryCall<T>(
                responseAsync: Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        private sealed class ThrowingStreamReader(RpcException failure)
            : IAsyncStreamReader<Contract.LogRegRetrainStreamEvent>
        {
            public Contract.LogRegRetrainStreamEvent Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                return Task.FromException<bool>(failure);
            }
        }
    }
}