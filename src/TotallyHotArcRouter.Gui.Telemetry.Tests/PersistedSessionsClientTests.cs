using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="PersistedSessionsClient"/> - the wire-to-DTO mapping and error translation behind
/// the Sessions tab's persisted-history load (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server, mirroring
/// <see cref="RoutingModeAdminClientTests"/>.
/// </remarks>
public class PersistedSessionsClientTests
{
    [Fact]
    public async Task ListAsync_MapsEveryFieldOffTheWire()
    {
        var createdAt = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        var response = new Contract.ListPersistedSessionsResponse { TranscriptCaptureEnabled = true };
        response.Transcripts.Add(new Contract.PersistedTranscript
        {
            SessionId = "sess-1",
            CorrelationId = "sess-1:1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(createdAt),
            RequestedModel = "gpt-5.4",
            RoutedModel = "kimi-k2.5",
            PromptText = "fix this bug",
            ResponseText = "here is the fix",
            CostUsd = "0.0042",
            InputTokens = 100,
            OutputTokens = 50,
            MemoryEntryId = 7,
        });
        var stub = new StubClient { Response = response };
        using var client = new PersistedSessionsClient(stub);

        var result = await client.ListAsync(10, TestContext.Current.CancellationToken);

        result.TranscriptCaptureEnabled.Should().BeTrue();
        var transcript = result.Transcripts.Should().ContainSingle().Subject;
        transcript.SessionId.Should().Be("sess-1");
        transcript.CorrelationId.Should().Be("sess-1:1");
        transcript.CreatedAtUtc.Should().Be(createdAt);
        transcript.RequestedModel.Should().Be("gpt-5.4");
        transcript.RoutedModel.Should().Be("kimi-k2.5");
        transcript.PromptText.Should().Be("fix this bug");
        transcript.ResponseText.Should().Be("here is the fix");
        transcript.CostUsd.Should().Be(0.0042m);
        transcript.InputTokens.Should().Be(100);
        transcript.OutputTokens.Should().Be(50);
        transcript.MemoryEntryId.Should().Be(7);
    }

    [Fact]
    public async Task ListAsync_RowWithNoOptionalFields_MapsThemToNull()
    {
        var response = new Contract.ListPersistedSessionsResponse { TranscriptCaptureEnabled = true };
        response.Transcripts.Add(new Contract.PersistedTranscript
        {
            SessionId = "sess-bare",
            CorrelationId = "sess-bare:1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            RequestedModel = "gpt-5.4",
            RoutedModel = "gpt-5.4",
        });
        var stub = new StubClient { Response = response };
        using var client = new PersistedSessionsClient(stub);

        var result = await client.ListAsync(10, TestContext.Current.CancellationToken);

        var transcript = result.Transcripts.Should().ContainSingle().Subject;
        transcript.PromptText.Should().BeNull();
        transcript.ResponseText.Should().BeNull();
        transcript.CostUsd.Should().BeNull();
        transcript.InputTokens.Should().BeNull();
        transcript.OutputTokens.Should().BeNull();
        transcript.MemoryEntryId.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_TranscriptCaptureDisabled_MapsFalseFlagWithEmptyList()
    {
        var response = new Contract.ListPersistedSessionsResponse { TranscriptCaptureEnabled = false };
        var stub = new StubClient { Response = response };
        using var client = new PersistedSessionsClient(stub);

        var result = await client.ListAsync(10, TestContext.Current.CancellationToken);

        result.TranscriptCaptureEnabled.Should().BeFalse();
        result.Transcripts.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_Unavailable_BecomesAPlainLanguageMessage()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new PersistedSessionsClient(stub);

        var ex = await Assert.ThrowsAsync<PersistedSessionsClientException>(
            () => client.ListAsync(10, TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read persisted sessions: the router is not reachable.");
        ex.InnerException.Should().BeOfType<RpcException>();
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_ARejection_KeepsTheServersOwnDetailAndIsNotFlaggedUnavailable()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Internal, "boom")) };
        using var client = new PersistedSessionsClient(stub);

        var ex = await Assert.ThrowsAsync<PersistedSessionsClientException>(
            () => client.ListAsync(10, TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read persisted sessions: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        var client = new PersistedSessionsClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        var client = new PersistedSessionsClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void The_default_address_overload_targets_the_proxys_grpc_port()
    {
        using var client = new PersistedSessionsClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PersistedSessionsClient((Contract.TelemetryService.TelemetryServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.TelemetryService.TelemetryServiceClient
    {
        public Contract.ListPersistedSessionsResponse Response { get; init; } = new();

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.ListPersistedSessionsResponse> ListPersistedSessionsAsync(
            Contract.ListPersistedSessionsRequest request,
            CallOptions options) =>
            new(
                Failure is null ? Task.FromResult(Response) : Task.FromException<Contract.ListPersistedSessionsResponse>(Failure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }
}
