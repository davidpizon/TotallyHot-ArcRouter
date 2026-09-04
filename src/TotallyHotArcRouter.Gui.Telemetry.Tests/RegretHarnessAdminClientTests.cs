using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="RegretHarnessAdminClient"/> - the wire-to-view mapping and error translation
/// behind the Governance → Regret Harness panel, mirroring <c>LogRegModelAdminClientTests</c>.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, same reasoning as
/// <c>LogRegModelAdminClientTests</c>: the generated client exposes a protected parameterless
/// constructor precisely for test doubles.
/// </remarks>
public class RegretHarnessAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_no_run_yet_maps_has_run_false()
    {
        var stub = new StubClient { StatusResponse = new Contract.RegretHarnessStatusResponse { HasRun = false } };
        using var client = new RegretHarnessAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.HasRun.Should().BeFalse();
        status.RanAtUtc.Should().BeNull();
        status.Message.Should().BeNull();
        status.Splits.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusAsync_a_prior_run_maps_its_message_and_splits()
    {
        var ranAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, offset: TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.RegretHarnessStatusResponse
            {
                HasRun = true,
                RanAtUtc = Timestamp.FromDateTimeOffset(ranAt),
                Message = "Completed: 2919 ID-test task(s), 176 OOD task(s) replayed.",
                Splits =
                {
                    new Contract.RegretHarnessSplitReport { SplitName = "ID test", MarkdownTable = "| Router |" }
                }
            }
        };
        using var client = new RegretHarnessAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.HasRun.Should().BeTrue();
        status.RanAtUtc.Should().Be(ranAt);
        status.Message.Should().Be("Completed: 2919 ID-test task(s), 176 OOD task(s) replayed.");
        status.Splits.Should().ContainSingle(s => s.SplitName == "ID test" && s.MarkdownTable == "| Router |");
    }

    [Fact]
    public async Task RunAsync_streams_stage_progress_then_the_result()
    {
        var stub = new StubClient
        {
            RunEvents =
            [
                new Contract.RegretHarnessStreamEvent
                {
                    StageProgress =
                        new Contract.RegretHarnessStageProgress { Stage = Contract.RegretHarnessStage.LoadingCorpus }
                },
                new Contract.RegretHarnessStreamEvent
                {
                    StageProgress = new Contract.RegretHarnessStageProgress
                        { Stage = Contract.RegretHarnessStage.BuildingReports }
                },
                new Contract.RegretHarnessStreamEvent
                {
                    Result = new Contract.RegretHarnessRunResult
                    {
                        Kind = Contract.RegretHarnessRunResultKind.Completed,
                        Message = "Completed: 1 task(s).",
                        RanAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        Splits = { new Contract.RegretHarnessSplitReport { SplitName = "OOD", MarkdownTable = "| x |" } }
                    }
                }
            ]
        };
        using var client = new RegretHarnessAdminClient(stub);

        var events = new List<RegretHarnessRunEvent>();
        await foreach (var e in client.RunAsync(TestContext.Current.CancellationToken)) events.Add(e);

        events.Should().HaveCount(3);
        events[0].StageProgress.Should().Be(RegretHarnessStageInfo.LoadingCorpus);
        events[0].Result.Should().BeNull();
        events[1].StageProgress.Should().Be(RegretHarnessStageInfo.BuildingReports);

        events[2].StageProgress.Should().BeNull();
        events[2].Result!.Kind.Should().Be(RegretHarnessRunResultKindInfo.Completed);
        events[2].Result!.Message.Should().Be("Completed: 1 task(s).");
        events[2].Result!.Splits.Should().ContainSingle(s => s.SplitName == "OOD");
    }

    [Theory]
    [InlineData(Contract.RegretHarnessRunResultKind.Completed, RegretHarnessRunResultKindInfo.Completed)]
    [InlineData(Contract.RegretHarnessRunResultKind.Declined, RegretHarnessRunResultKindInfo.Declined)]
    [InlineData(Contract.RegretHarnessRunResultKind.AlreadyRunning, RegretHarnessRunResultKindInfo.AlreadyRunning)]
    // Unspecified stands in for a value this build cannot map. It must not read as Completed: the panel
    // would otherwise report success for an outcome nobody actually asserted.
    [InlineData(Contract.RegretHarnessRunResultKind.Unspecified, RegretHarnessRunResultKindInfo.Declined)]
    public async Task RunAsync_maps_result_kinds(
        Contract.RegretHarnessRunResultKind wireKind,
        RegretHarnessRunResultKindInfo expected)
    {
        var stub = new StubClient
        {
            RunEvents =
            [
                new Contract.RegretHarnessStreamEvent
                {
                    Result = new Contract.RegretHarnessRunResult { Kind = wireKind, Message = "m" }
                }
            ]
        };
        using var client = new RegretHarnessAdminClient(stub);

        var events = new List<RegretHarnessRunEvent>();
        await foreach (var e in client.RunAsync(TestContext.Current.CancellationToken)) events.Add(e);

        events.Single().Result!.Kind.Should().Be(expected);
    }

    [Fact]
    public async Task RunAsync_an_empty_oneof_maps_to_an_all_null_event_without_throwing()
    {
        var stub = new StubClient { RunEvents = [new Contract.RegretHarnessStreamEvent()] };
        using var client = new RegretHarnessAdminClient(stub);

        var events = new List<RegretHarnessRunEvent>();
        await foreach (var e in client.RunAsync(TestContext.Current.CancellationToken)) events.Add(e);

        var single = events.Single();
        single.StageProgress.Should().BeNull();
        single.Result.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_wraps_a_mid_stream_failure()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new RegretHarnessAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RegretHarnessAdminException>(async () =>
        {
            await foreach (var _ in client.RunAsync(TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Message.Should().Be("Regret harness run failed: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        using var client = new RegretHarnessAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RegretHarnessAdminException>(() =>
            client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the regret harness status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        using var client = new RegretHarnessAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RegretHarnessAdminException>(() =>
            client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the regret harness status: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new RegretHarnessAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new RegretHarnessAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RegretHarnessAdminClient((Contract.RegretHarnessAdminService.RegretHarnessAdminServiceClient)null!));
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
    private sealed class StubClient : Contract.RegretHarnessAdminService.RegretHarnessAdminServiceClient
    {
        public Contract.RegretHarnessStatusResponse StatusResponse { get; init; } = new();

        public IReadOnlyList<Contract.RegretHarnessStreamEvent> RunEvents { get; init; } = [];

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.RegretHarnessStatusResponse> GetRegretHarnessStatusAsync(
            Contract.GetRegretHarnessStatusRequest request,
            CallOptions options)
        {
            return Call(StatusResponse);
        }

        public override AsyncServerStreamingCall<Contract.RegretHarnessStreamEvent> RunRegretHarness(
            Contract.RunRegretHarnessRequest request,
            CallOptions options)
        {
            IAsyncStreamReader<Contract.RegretHarnessStreamEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.RegretHarnessStreamEvent>(RunEvents)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.RegretHarnessStreamEvent>(
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
            : IAsyncStreamReader<Contract.RegretHarnessStreamEvent>
        {
            public Contract.RegretHarnessStreamEvent Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                return Task.FromException<bool>(failure);
            }
        }
    }
}
