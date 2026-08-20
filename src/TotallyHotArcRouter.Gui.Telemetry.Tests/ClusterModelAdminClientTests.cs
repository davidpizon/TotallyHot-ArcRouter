using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="ClusterModelAdminClient"/> - the wire-to-view mapping and error translation
/// behind the Governance → Cluster Model panel, mirroring <c>BenchmarkDataAdminClientTests</c>.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, same reasoning as
/// <c>BenchmarkDataAdminClientTests</c>: the generated client exposes a protected parameterless
/// constructor precisely for test doubles.
/// </remarks>
public class ClusterModelAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_no_artifact_maps_artifact_present_false()
    {
        var stub = new StubClient
        {
            StatusResponse = new Contract.ClusterModelStatusResponse
            {
                ArtifactPresent = false,
                EntriesSinceLastRetrain = 42,
                RetentionDays = 30,
                MaxTranscriptRows = 50_000,
                CurrentTranscriptRowCount = 100,
            },
        };
        using var client = new ClusterModelAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ArtifactPresent.Should().BeFalse();
        status.ChosenK.Should().Be(0);
        status.TrainedAtUtc.Should().BeNull();
        status.EntriesSinceLastRetrain.Should().Be(42);
        status.Clusters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusAsync_with_artifact_maps_clusters_and_trained_at()
    {
        var trainedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.ClusterModelStatusResponse
            {
                ArtifactPresent = true,
                ChosenK = 2,
                TrainedAtUtc = Timestamp.FromDateTimeOffset(trainedAt),
                TrainedFrom = "bootstrap_tasks=20, memory_entries=5",
                BootstrapTaskCount = 20,
                MemoryEntryCount = 5,
                EntriesSinceLastRetrain = 3,
                RetentionDays = 30,
                MaxTranscriptRows = 50_000,
                CurrentTranscriptRowCount = 8,
                ClusterSizes = { 10, 15 },
                ClusterNames = { "mostly bug_fixing: sql, migration", "mostly test_generation" },
            },
        };
        using var client = new ClusterModelAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ArtifactPresent.Should().BeTrue();
        status.ChosenK.Should().Be(2);
        status.TrainedAtUtc.Should().Be(trainedAt);
        status.TrainedFrom.Should().Be("bootstrap_tasks=20, memory_entries=5");
        status.Clusters.Should().HaveCount(2);
        status.Clusters[0].Size.Should().Be(10);
        status.Clusters[0].Name.Should().Be("mostly bug_fixing: sql, migration");
        status.Clusters[1].Size.Should().Be(15);
    }

    [Fact]
    public async Task RetrainAsync_streams_bootstrap_progress_then_the_result()
    {
        var stub = new StubClient
        {
            RetrainEvents =
            [
                new Contract.ClusterRetrainStreamEvent
                {
                    BootstrapProgress = new Contract.ClusterRetrainBootstrapProgress { TasksEmbedded = 5 },
                },
                new Contract.ClusterRetrainStreamEvent
                {
                    BootstrapProgress = new Contract.ClusterRetrainBootstrapProgress { TasksEmbedded = 10 },
                },
                new Contract.ClusterRetrainStreamEvent
                {
                    Result = new Contract.ClusterRetrainResult
                    {
                        Kind = Contract.ClusterRetrainResultKind.Trained,
                        Message = "Trained 2 clusters.",
                        Status = new Contract.ClusterModelStatusResponse { ArtifactPresent = true, ChosenK = 2 },
                    },
                },
            ],
        };
        using var client = new ClusterModelAdminClient(stub);

        var events = new List<ClusterRetrainEvent>();
        await foreach (var e in client.RetrainAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Should().HaveCount(3);
        events[0].BootstrapProgress!.TasksEmbedded.Should().Be(5);
        events[0].Result.Should().BeNull();
        events[1].BootstrapProgress!.TasksEmbedded.Should().Be(10);

        events[2].BootstrapProgress.Should().BeNull();
        events[2].Result!.Kind.Should().Be(ClusterRetrainResultKindInfo.Trained);
        events[2].Result!.Message.Should().Be("Trained 2 clusters.");
        events[2].Result!.Status.ChosenK.Should().Be(2);
    }

    [Theory]
    [InlineData(Contract.ClusterRetrainResultKind.Trained, ClusterRetrainResultKindInfo.Trained)]
    [InlineData(Contract.ClusterRetrainResultKind.Declined, ClusterRetrainResultKindInfo.Declined)]
    [InlineData(Contract.ClusterRetrainResultKind.AlreadyRunning, ClusterRetrainResultKindInfo.AlreadyRunning)]
    // Unspecified stands in for a value this build cannot map. It must not read as Trained: the panel
    // would otherwise report success for an outcome nobody actually asserted.
    [InlineData(Contract.ClusterRetrainResultKind.Unspecified, ClusterRetrainResultKindInfo.Declined)]
    public async Task RetrainAsync_maps_result_kinds(
        Contract.ClusterRetrainResultKind wireKind,
        ClusterRetrainResultKindInfo expected)
    {
        var stub = new StubClient
        {
            RetrainEvents = [new Contract.ClusterRetrainStreamEvent
            {
                Result = new Contract.ClusterRetrainResult { Kind = wireKind, Message = "m", Status = new Contract.ClusterModelStatusResponse() },
            }],
        };
        using var client = new ClusterModelAdminClient(stub);

        var events = new List<ClusterRetrainEvent>();
        await foreach (var e in client.RetrainAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Single().Result!.Kind.Should().Be(expected);
    }

    [Fact]
    public async Task RetrainAsync_an_empty_oneof_maps_to_an_all_null_event_without_throwing()
    {
        var stub = new StubClient { RetrainEvents = [new Contract.ClusterRetrainStreamEvent()] };
        using var client = new ClusterModelAdminClient(stub);

        var events = new List<ClusterRetrainEvent>();
        await foreach (var e in client.RetrainAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        var single = events.Single();
        single.BootstrapProgress.Should().BeNull();
        single.Result.Should().BeNull();
    }

    [Fact]
    public async Task RetrainAsync_wraps_a_mid_stream_failure()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new ClusterModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<ClusterModelAdminException>(async () =>
        {
            await foreach (var _ in client.RetrainAsync(TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Message.Should().Be("Cluster model retrain failed: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new ClusterModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<ClusterModelAdminException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the cluster model status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Internal, "boom")) };
        using var client = new ClusterModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<ClusterModelAdminException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the cluster model status: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new ClusterModelAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new ClusterModelAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ClusterModelAdminClient((Contract.ClusterModelAdminService.ClusterModelAdminServiceClient)null!));
    }

    /// <summary>An <see cref="IAsyncStreamReader{T}"/> fake that yields a fixed, in-memory sequence of messages.</summary>
    private sealed class FakeStreamReader<T>(IReadOnlyList<T> messages) : IAsyncStreamReader<T>
    {
        private int _index = -1;

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            if (_index >= messages.Count)
            {
                return Task.FromResult(false);
            }

            Current = messages[_index];
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.ClusterModelAdminService.ClusterModelAdminServiceClient
    {
        public Contract.ClusterModelStatusResponse StatusResponse { get; init; } = new();

        public IReadOnlyList<Contract.ClusterRetrainStreamEvent> RetrainEvents { get; init; } = [];

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.ClusterModelStatusResponse> GetClusterModelStatusAsync(
            Contract.GetClusterModelStatusRequest request,
            CallOptions options) =>
            Call(StatusResponse);

        public override AsyncServerStreamingCall<Contract.ClusterRetrainStreamEvent> RetrainClusterModel(
            Contract.RetrainClusterModelRequest request,
            CallOptions options)
        {
            IAsyncStreamReader<Contract.ClusterRetrainStreamEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.ClusterRetrainStreamEvent>(RetrainEvents)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.ClusterRetrainStreamEvent>(
                reader,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        private AsyncUnaryCall<T> Call<T>(T response) =>
            new(
                Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        private sealed class ThrowingStreamReader(RpcException failure) : IAsyncStreamReader<Contract.ClusterRetrainStreamEvent>
        {
            public Contract.ClusterRetrainStreamEvent Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(failure);
        }
    }
}
