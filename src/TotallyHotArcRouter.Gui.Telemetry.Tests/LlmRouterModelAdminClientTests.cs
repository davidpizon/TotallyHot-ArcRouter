using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="LlmRouterModelAdminClient"/> - the wire-to-view mapping and error translation
/// behind the Governance → Benchmark Data panel's "Local Voter Model" section, mirroring
/// <c>BenchmarkDataAdminClientTests</c>.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, same reasoning as
/// <c>BenchmarkDataAdminClientTests</c>: the generated client exposes a protected parameterless
/// constructor precisely for test doubles.
/// </remarks>
public class LlmRouterModelAdminClientTests
{
    [Fact]
    public async Task GetStatusAsync_maps_base_url_and_current_flag()
    {
        var stub = new StubClient
        {
            StatusResponse = new Contract.LlmRouterModelStatusResponse
            {
                BaseUrl = "https://huggingface.co/org/model/resolve/main",
                Current = true,
            },
        };
        using var client = new LlmRouterModelAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        status.BaseUrl.Should().Be("https://huggingface.co/org/model/resolve/main");
        status.Current.Should().BeTrue();
    }

    [Fact]
    public async Task GetStatusAsync_maps_every_file_including_unsynced_and_optional_ones()
    {
        var syncedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var stub = new StubClient
        {
            StatusResponse = new Contract.LlmRouterModelStatusResponse
            {
                Files =
                {
                    new Contract.LlmRouterModelFile
                    {
                        FileName = "model.onnx",
                        Synced = true,
                        SizeBytes = 42,
                        SyncedAtUtc = Timestamp.FromDateTimeOffset(syncedAt),
                        ChecksumVerified = true,
                    },
                    new Contract.LlmRouterModelFile
                    {
                        FileName = "model.onnx.data",
                        Synced = false,
                        IsOptional = true,
                    },
                },
            },
        };
        using var client = new LlmRouterModelAdminClient(stub);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        var synced = status.Files.Single(f => f.FileName == "model.onnx");
        synced.Synced.Should().BeTrue();
        synced.SizeBytes.Should().Be(42);
        synced.SyncedAtUtc.Should().Be(syncedAt);
        synced.ChecksumVerified.Should().BeTrue();
        synced.IsOptional.Should().BeFalse();

        var optional = status.Files.Single(f => f.FileName == "model.onnx.data");
        optional.Synced.Should().BeFalse();
        optional.SyncedAtUtc.Should().BeNull();
        optional.IsOptional.Should().BeTrue();
    }

    [Fact]
    public async Task SetBaseUrlAsync_sends_the_url_and_maps_the_resulting_status()
    {
        var stub = new StubClient
        {
            SetBaseUrlResponse = new Contract.LlmRouterModelStatusResponse
            {
                BaseUrl = "https://huggingface.co/org/other-model/resolve/main",
                Current = false,
            },
        };
        using var client = new LlmRouterModelAdminClient(stub);

        var status = await client.SetBaseUrlAsync(
            "https://huggingface.co/org/other-model/resolve/main",
            TestContext.Current.CancellationToken);

        stub.LastSetBaseUrlRequest!.BaseUrl.Should().Be("https://huggingface.co/org/other-model/resolve/main");
        status.BaseUrl.Should().Be("https://huggingface.co/org/other-model/resolve/main");
        status.Current.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetBaseUrlAsync_rejects_a_blank_url(string baseUrl)
    {
        using var client = new LlmRouterModelAdminClient(new StubClient());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SetBaseUrlAsync(baseUrl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBaseUrlAsync_rejects_a_null_url()
    {
        using var client = new LlmRouterModelAdminClient(new StubClient());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.SetBaseUrlAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncAsync_streams_progress_then_the_final_status()
    {
        var stub = new StubClient
        {
            SyncEvents =
            [
                new Contract.LlmRouterModelSyncStreamEvent
                {
                    Progress = new Contract.LlmRouterModelSyncProgressEvent
                    {
                        FileName = "model.onnx",
                        Stage = Contract.LlmRouterModelSyncStage.Downloading,
                        BytesTransferred = 100,
                    },
                },
                new Contract.LlmRouterModelSyncStreamEvent
                {
                    Progress = new Contract.LlmRouterModelSyncProgressEvent
                    {
                        FileName = "model.onnx",
                        Stage = Contract.LlmRouterModelSyncStage.Completed,
                    },
                },
                new Contract.LlmRouterModelSyncStreamEvent
                {
                    FinalStatus = new Contract.LlmRouterModelStatusResponse { Current = true },
                },
            ],
        };
        using var client = new LlmRouterModelAdminClient(stub);

        var events = new List<LlmRouterModelSyncEvent>();
        await foreach (var e in client.SyncAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Should().HaveCount(3);
        events[0].Progress!.Stage.Should().Be(LlmRouterModelSyncStageInfo.Downloading);
        events[0].Progress!.BytesTransferred.Should().Be(100);
        events[0].FinalStatus.Should().BeNull();

        events[1].Progress!.Stage.Should().Be(LlmRouterModelSyncStageInfo.Completed);

        events[2].Progress.Should().BeNull();
        events[2].FinalStatus!.Current.Should().BeTrue();
    }

    [Fact]
    public async Task SyncAsync_maps_a_failed_progress_events_error()
    {
        var stub = new StubClient
        {
            SyncEvents =
            [
                new Contract.LlmRouterModelSyncStreamEvent
                {
                    Progress = new Contract.LlmRouterModelSyncProgressEvent
                    {
                        FileName = "model.onnx",
                        Stage = Contract.LlmRouterModelSyncStage.Failed,
                        Error = "checksum mismatch",
                    },
                },
            ],
        };
        using var client = new LlmRouterModelAdminClient(stub);

        var events = new List<LlmRouterModelSyncEvent>();
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
        using var client = new LlmRouterModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<LlmRouterModelAdminException>(async () =>
        {
            await foreach (var _ in client.SyncAsync(TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Message.Should().Be("llm_router model sync failed: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new LlmRouterModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<LlmRouterModelAdminException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the llm_router model status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Internal, "boom")) };
        using var client = new LlmRouterModelAdminClient(stub);

        var ex = await Assert.ThrowsAsync<LlmRouterModelAdminException>(
            () => client.SetBaseUrlAsync("https://huggingface.co/org/model/resolve/main", TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not switch the llm_router model: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new LlmRouterModelAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new LlmRouterModelAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LlmRouterModelAdminClient((Contract.LlmRouterModelAdminService.LlmRouterModelAdminServiceClient)null!));
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
    private sealed class StubClient : Contract.LlmRouterModelAdminService.LlmRouterModelAdminServiceClient
    {
        public Contract.LlmRouterModelStatusResponse StatusResponse { get; init; } = new();

        public Contract.LlmRouterModelStatusResponse SetBaseUrlResponse { get; init; } = new();

        public IReadOnlyList<Contract.LlmRouterModelSyncStreamEvent> SyncEvents { get; init; } = [];

        public RpcException? Failure { get; init; }

        public Contract.SetLlmRouterModelBaseUrlRequest? LastSetBaseUrlRequest { get; private set; }

        public override AsyncUnaryCall<Contract.LlmRouterModelStatusResponse> GetLlmRouterModelStatusAsync(
            Contract.GetLlmRouterModelStatusRequest request,
            CallOptions options) =>
            Call(StatusResponse);

        public override AsyncUnaryCall<Contract.LlmRouterModelStatusResponse> SetLlmRouterModelBaseUrlAsync(
            Contract.SetLlmRouterModelBaseUrlRequest request,
            CallOptions options)
        {
            LastSetBaseUrlRequest = request;
            return Call(SetBaseUrlResponse);
        }

        public override AsyncServerStreamingCall<Contract.LlmRouterModelSyncStreamEvent> SyncLlmRouterModel(
            Contract.SyncLlmRouterModelRequest request,
            CallOptions options)
        {
            IAsyncStreamReader<Contract.LlmRouterModelSyncStreamEvent> reader = Failure is null
                ? new FakeStreamReader<Contract.LlmRouterModelSyncStreamEvent>(SyncEvents)
                : new ThrowingStreamReader(Failure);

            return new AsyncServerStreamingCall<Contract.LlmRouterModelSyncStreamEvent>(
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

        private sealed class ThrowingStreamReader(RpcException failure) : IAsyncStreamReader<Contract.LlmRouterModelSyncStreamEvent>
        {
            public Contract.LlmRouterModelSyncStreamEvent Current => throw failure;

            public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(failure);
        }
    }
}
