using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="BenchmarkDataAdminClient"/> - the wire-to-view mapping and error translation
/// behind the Governance → Benchmark Data panel, mirroring <c>PriceSourceAdminClientTests</c>.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, same reasoning as
/// <c>PriceSourceAdminClientTests</c>: the generated client exposes a protected parameterless
/// constructor precisely for test doubles.
/// </remarks>
public class BenchmarkDataAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_maps_state_reason_and_checked_at()
    {
        var checkedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.BenchmarkStatusResponse
            {
                State = Contract.BenchmarkDataState.CheckFailed,
                Reason = "failed to connect",
                CheckedAtUtc = Timestamp.FromDateTimeOffset(checkedAt),
            },
        };
        using var client = new BenchmarkDataAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.State.Should().Be(BenchmarkDataAdminState.CheckFailed);
        status.Reason.Should().Be("failed to connect");
        status.CheckedAtUtc.Should().Be(checkedAt);
    }

    [Fact]
    public async Task GetStatusAsync_maps_every_file_including_unsynced_ones()
    {
        var syncedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.BenchmarkStatusResponse
            {
                State = Contract.BenchmarkDataState.Update,
                Files =
                {
                    new Contract.BenchmarkFile { FileName = "models.json", Synced = true, SizeBytes = 42, RowCount = 3, SyncedAtUtc = Timestamp.FromDateTimeOffset(syncedAt) },
                    new Contract.BenchmarkFile { FileName = "summary.json", Synced = false },
                },
            },
        };
        using var client = new BenchmarkDataAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        var synced = status.Files.Single(f => f.FileName == "models.json");
        synced.Synced.Should().BeTrue();
        synced.SizeBytes.Should().Be(42);
        synced.RowCount.Should().Be(3);
        synced.SyncedAtUtc.Should().Be(syncedAt);

        var unsynced = status.Files.Single(f => f.FileName == "summary.json");
        unsynced.Synced.Should().BeFalse();
        unsynced.SyncedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(Contract.BenchmarkDataState.Current, BenchmarkDataAdminState.Current)]
    [InlineData(Contract.BenchmarkDataState.Update, BenchmarkDataAdminState.Update)]
    [InlineData(Contract.BenchmarkDataState.CheckFailed, BenchmarkDataAdminState.CheckFailed)]
    // Unspecified is what the service emits for a state it could not map, and stands in for any value
    // added to the contract after this build. It must not read as Update: the panel offers a sync against
    // that state, so an unmapped value would invite a blind download of the whole corpus.
    [InlineData(Contract.BenchmarkDataState.Unspecified, BenchmarkDataAdminState.CheckFailed)]
    [InlineData((Contract.BenchmarkDataState)99, BenchmarkDataAdminState.CheckFailed)]
    public async Task GetStatusAsync_maps_unknown_states_to_check_failed(
        Contract.BenchmarkDataState wireState,
        BenchmarkDataAdminState expected)
    {
        var stub = new StubClient { StatusResponse = new Contract.BenchmarkStatusResponse { State = wireState } };
        using var client = new BenchmarkDataAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.State.Should().Be(expected);
    }

    [Fact]
    public async Task GetStatusAsync_with_no_reason_maps_to_null()
    {
        var stub = new StubClient { StatusResponse = new Contract.BenchmarkStatusResponse { State = Contract.BenchmarkDataState.Current } };
        using var client = new BenchmarkDataAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.Reason.Should().BeNull();
    }

    [Fact]
    public async Task RecheckAsync_maps_the_recomputed_status()
    {
        var stub = new StubClient { RecheckResponse = new Contract.BenchmarkStatusResponse { State = Contract.BenchmarkDataState.Current } };
        using var client = new BenchmarkDataAdminClient(stub);

        var status = await client.RecheckAsync(TestContext.Current.CancellationToken);

        status.State.Should().Be(BenchmarkDataAdminState.Current);
    }

    [Fact]
    public async Task SyncAsync_streams_progress_then_the_final_status()
    {
        var stub = new StubClient
        {
            SyncEvents =
            [
                new Contract.BenchmarkSyncStreamEvent
                {
                    Progress = new Contract.BenchmarkSyncProgressEvent
                    {
                        FileName = "models.json",
                        Stage = Contract.BenchmarkSyncStage.Downloading,
                        BytesTransferred = 100,
                    },
                },
                new Contract.BenchmarkSyncStreamEvent
                {
                    Progress = new Contract.BenchmarkSyncProgressEvent
                    {
                        FileName = "models.json",
                        Stage = Contract.BenchmarkSyncStage.Completed,
                        RowsImported = 3,
                    },
                },
                new Contract.BenchmarkSyncStreamEvent
                {
                    FinalStatus = new Contract.BenchmarkStatusResponse { State = Contract.BenchmarkDataState.Current },
                },
            ],
        };
        using var client = new BenchmarkDataAdminClient(stub);

        var events = new List<BenchmarkSyncEvent>();
        await foreach (var e in client.SyncAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Should().HaveCount(3);
        events[0].Progress!.Stage.Should().Be(BenchmarkSyncStageInfo.Downloading);
        events[0].Progress!.BytesTransferred.Should().Be(100);
        events[0].FinalStatus.Should().BeNull();

        events[1].Progress!.Stage.Should().Be(BenchmarkSyncStageInfo.Completed);
        events[1].Progress!.RowsImported.Should().Be(3);

        events[2].Progress.Should().BeNull();
        events[2].FinalStatus!.State.Should().Be(BenchmarkDataAdminState.Current);
    }

    [Fact]
    public async Task SyncAsync_maps_a_failed_progress_events_error()
    {
        var stub = new StubClient
        {
            SyncEvents =
            [
                new Contract.BenchmarkSyncStreamEvent
                {
                    Progress = new Contract.BenchmarkSyncProgressEvent
                    {
                        FileName = "models.json",
                        Stage = Contract.BenchmarkSyncStage.Failed,
                        Error = "checksum mismatch",
                    },
                },
            ],
        };
        using var client = new BenchmarkDataAdminClient(stub);

        var events = new List<BenchmarkSyncEvent>();
        await foreach (var e in client.SyncAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Single().Progress!.Error.Should().Be("checksum mismatch");
    }

    [Fact]
    public async Task SyncAsync_wraps_a_mid_stream_failure()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new BenchmarkDataAdminClient(stub);

        var ex = await Assert.ThrowsAsync<BenchmarkDataAdminException>(async () =>
        {
            await foreach (var _ in client.SyncAsync(TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Message.Should().Be("Benchmark data sync failed: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new BenchmarkDataAdminClient(stub);

        var ex = await Assert.ThrowsAsync<BenchmarkDataAdminException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the benchmark data status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Internal, "boom")) };
        using var client = new BenchmarkDataAdminClient(stub);

        var ex = await Assert.ThrowsAsync<BenchmarkDataAdminException>(() => client.RecheckAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not recheck the benchmark data: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new BenchmarkDataAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new BenchmarkDataAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BenchmarkDataAdminClient((Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient)null!));
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
    private sealed class StubClient : Contract.BenchmarkDataAdminService.BenchmarkDataAdminServiceClient
    {
        public Contract.BenchmarkStatusResponse StatusResponse { get; init; } = new();

        public Contract.BenchmarkStatusResponse RecheckResponse { get; init; } = new();

        public IReadOnlyList<Contract.BenchmarkSyncStreamEvent> SyncEvents { get; init; } = [];

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.BenchmarkStatusResponse> GetBenchmarkStatusAsync(
            Contract.GetBenchmarkStatusRequest request,
            CallOptions options) =>
            Call(StatusResponse);

        public override AsyncUnaryCall<Contract.BenchmarkStatusResponse> RecheckBenchmarkDataAsync(
            Contract.RecheckBenchmarkDataRequest request,
            CallOptions options) =>
            Call(RecheckResponse);

        public override AsyncServerStreamingCall<Contract.BenchmarkSyncStreamEvent> SyncBenchmarkData(
            Contract.SyncBenchmarkDataRequest request,
            CallOptions options)
        {
            IAsyncStreamReader<Contract.BenchmarkSyncStreamEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.BenchmarkSyncStreamEvent>(SyncEvents)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.BenchmarkSyncStreamEvent>(
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

        private sealed class ThrowingStreamReader(RpcException failure) : IAsyncStreamReader<Contract.BenchmarkSyncStreamEvent>
        {
            public Contract.BenchmarkSyncStreamEvent Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(failure);
        }
    }
}
