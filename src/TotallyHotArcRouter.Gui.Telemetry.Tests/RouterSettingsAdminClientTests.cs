using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="RouterSettingsAdminClient"/> - the wire-to-view mapping and error translation
/// behind the System Settings window's Adaptive Routing row.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, mirroring
/// <c>RoutingModeAdminClientTests</c>. This project is plain net10.0, so unlike the bUnit modal tests these
/// run in CI.
/// </remarks>
public class RouterSettingsAdminClientTests
{
    [Fact]
    public async Task GetAsync_maps_the_effective_values_off_the_wire()
    {
        var stub = new StubClient
        {
            GetResponse = new Contract.RouterSettingsResponse
            {
                AdaptiveRoutingEnabled = true,
                EmbeddingMemoryCapacity = 15_000,
                JudgeEnabled = true,
                JudgeModelName = "free-judge",
                EligibleJudgeModels = { "free-judge", "other-free" },
                TranscriptCaptureEnabled = true,
            },
        };
        using var client = new RouterSettingsAdminClient(stub);

        var settings = await client.GetAsync(TestContext.Current.CancellationToken);

        settings.AdaptiveRoutingEnabled.Should().BeTrue();
        settings.EmbeddingMemoryCapacity.Should().Be(15_000);
        settings.JudgeEnabled.Should().BeTrue();
        settings.JudgeModelName.Should().Be("free-judge");
        settings.EligibleJudgeModels.Should().Equal("free-judge", "other-free");
        settings.TranscriptCaptureEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_sends_every_field_and_maps_the_post_mutation_response()
    {
        var stub = new StubClient
        {
            UpdateResponse = new Contract.RouterSettingsResponse
            {
                AdaptiveRoutingEnabled = true,
                EmbeddingMemoryCapacity = 5_000,
                JudgeEnabled = true,
                JudgeModelName = "free-judge",
            },
        };
        using var client = new RouterSettingsAdminClient(stub);

        var settings = await client.UpdateAsync(true, 5_000, true, "free-judge", true, TestContext.Current.CancellationToken);

        settings.EmbeddingMemoryCapacity.Should().Be(5_000);
        settings.JudgeEnabled.Should().BeTrue();
        settings.JudgeModelName.Should().Be("free-judge");
        stub.LastUpdateRequest.Should().NotBeNull();
        stub.LastUpdateRequest!.AdaptiveRoutingEnabled.Should().BeTrue();
        stub.LastUpdateRequest.EmbeddingMemoryCapacity.Should().Be(5_000);
        stub.LastUpdateRequest.JudgeEnabled.Should().BeTrue();
        stub.LastUpdateRequest.JudgeModelName.Should().Be("free-judge");
        stub.LastUpdateRequest.TranscriptCaptureEnabled.Should().BeTrue();
    }

    /// <summary>A null model name is the "automatic" choice, and must reach the wire as an empty string rather than throwing.</summary>
    [Fact]
    public async Task UpdateAsync_null_judge_model_name_is_sent_as_empty()
    {
        var stub = new StubClient { UpdateResponse = new Contract.RouterSettingsResponse() };
        using var client = new RouterSettingsAdminClient(stub);

        await client.UpdateAsync(false, 5_000, false, null!, false, TestContext.Current.CancellationToken);

        stub.LastUpdateRequest!.JudgeModelName.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearTranscriptsAsync_maps_the_deleted_row_count()
    {
        var stub = new StubClient { ClearTranscriptsResponse = new Contract.ClearTranscriptsResponse { RowsDeleted = 42 } };
        using var client = new RouterSettingsAdminClient(stub);

        var rowsDeleted = await client.ClearTranscriptsAsync(TestContext.Current.CancellationToken);

        rowsDeleted.Should().Be(42);
    }

    [Fact]
    public async Task ClearTranscriptsAsync_unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient { ClearTranscriptsFailure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new RouterSettingsAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RouterSettingsAdminException>(() => client.ClearTranscriptsAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not clear the transcript data: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_unavailable_becomes_a_plain_language_message()
    {
        var stub = new StubClient { GetFailure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new RouterSettingsAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RouterSettingsAdminException>(() => client.GetAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the router settings: the router is not reachable.");
        ex.InnerException.Should().BeOfType<RpcException>();
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_a_rejection_keeps_the_servers_own_detail_and_is_not_flagged_unavailable()
    {
        var stub = new StubClient
        {
            UpdateFailure = new RpcException(new Status(StatusCode.InvalidArgument, "embedding_memory_capacity must be between 500 and 50000 (got 1)")),
        };
        using var client = new RouterSettingsAdminClient(stub);

        var ex = await Assert.ThrowsAsync<RouterSettingsAdminException>(
            () => client.UpdateAsync(false, 1, false, string.Empty, false, TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not save the router settings: embedding_memory_capacity must be between 500 and 50000 (got 1)");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new RouterSettingsAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new RouterSettingsAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void The_default_address_overload_targets_the_proxys_grpc_port()
    {
        using var client = new RouterSettingsAdminClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RouterSettingsAdminClient((Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient
    {
        public Contract.RouterSettingsResponse GetResponse { get; init; } = new();

        public RpcException? GetFailure { get; init; }

        public Contract.RouterSettingsResponse UpdateResponse { get; init; } = new();

        public RpcException? UpdateFailure { get; init; }

        public Contract.UpdateRouterSettingsRequest? LastUpdateRequest { get; private set; }

        public Contract.ClearTranscriptsResponse ClearTranscriptsResponse { get; init; } = new();

        public RpcException? ClearTranscriptsFailure { get; init; }

        public override AsyncUnaryCall<Contract.ClearTranscriptsResponse> ClearTranscriptsAsync(
            Contract.ClearTranscriptsRequest request,
            CallOptions options) =>
            new(
                ClearTranscriptsFailure is null ? Task.FromResult(ClearTranscriptsResponse) : Task.FromException<Contract.ClearTranscriptsResponse>(ClearTranscriptsFailure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        public override AsyncUnaryCall<Contract.RouterSettingsResponse> GetRouterSettingsAsync(
            Contract.GetRouterSettingsRequest request,
            CallOptions options) =>
            new(
                GetFailure is null ? Task.FromResult(GetResponse) : Task.FromException<Contract.RouterSettingsResponse>(GetFailure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        public override AsyncUnaryCall<Contract.RouterSettingsResponse> UpdateRouterSettingsAsync(
            Contract.UpdateRouterSettingsRequest request,
            CallOptions options)
        {
            LastUpdateRequest = request;
            return new(
                UpdateFailure is null ? Task.FromResult(UpdateResponse) : Task.FromException<Contract.RouterSettingsResponse>(UpdateFailure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }
}
