using AwesomeAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="PersistedSessionsClient"/> - the wire-to-DTO mapping and error translation behind
/// the Sessions tab's persisted-history load (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
/// <remarks>
/// Mostly driven through a subclassed generated stub rather than a live server, mirroring
/// <see cref="RoutingModeAdminClientTests"/>. The receive-cap tests are the exception: they go through a real
/// <see cref="GrpcChannel"/> built like the browser GUI's, because a stub skips the client library's
/// message-size check.
/// </remarks>
public class PersistedSessionsClientTests
{
    [Fact]
    public async Task ListAsync_MapsEveryFieldOffTheWire()
    {
        var createdAt = new DateTimeOffset(2026, 7, 8, 12, 0, 0, offset: TimeSpan.Zero);
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
            MemoryEntryId = 7
        });
        var stub = new StubClient { Response = response };
        var client = new PersistedSessionsClient(stub);

        var result = await client.ListAsync(10, cancellationToken: TestContext.Current.CancellationToken);

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
            RoutedModel = "gpt-5.4"
        });
        var stub = new StubClient { Response = response };
        var client = new PersistedSessionsClient(stub);

        var result = await client.ListAsync(10, cancellationToken: TestContext.Current.CancellationToken);

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
        var client = new PersistedSessionsClient(stub);

        var result = await client.ListAsync(10, cancellationToken: TestContext.Current.CancellationToken);

        result.TranscriptCaptureEnabled.Should().BeFalse();
        result.Transcripts.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_Unavailable_BecomesAPlainLanguageMessage()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Unavailable, detail: "failed to connect")) };
        var client = new PersistedSessionsClient(stub);

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.ListAsync(10, cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read persisted sessions: the router is not reachable.");
        ex.InnerException.Should().BeOfType<RpcException>();
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_ARejection_KeepsTheServersOwnDetailAndIsNotFlaggedUnavailable()
    {
        var stub = new StubClient
        { Failure = new RpcException(new Status(statusCode: StatusCode.Internal, detail: "boom")) };
        var client = new PersistedSessionsClient(stub);

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.ListAsync(10, cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read persisted sessions: boom");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PersistedSessionsClient((Contract.TelemetryService.TelemetryServiceClient)null!));
    }

    [Fact]
    public void The_client_librarys_default_receive_cap_is_four_mebibytes()
    {
        new GrpcChannelOptions().MaxReceiveMessageSize.Should().Be(4 * 1024 * 1024);
    }

    [Fact]
    public async Task ListAsync_OverAWasmShapedChannel_ReceivesAResponseOfExactlyTheDefaultCap()
    {
        using var channel = WasmShapedChannel(ResponseOfSerializedSize(4 * 1024 * 1024));
        var client = new PersistedSessionsClient(channel.CreateCallInvoker());

        var result = await client.ListAsync(500, cancellationToken: TestContext.Current.CancellationToken);

        result.Transcripts.Should().ContainSingle();
    }

    [Fact]
    public async Task ListAsync_OverAWasmShapedChannel_RejectsAResponseOneByteOverTheDefaultCapAsResourceExhausted()
    {
        using var channel = WasmShapedChannel(ResponseOfSerializedSize(4 * 1024 * 1024 + 1));
        var client = new PersistedSessionsClient(channel.CreateCallInvoker());

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            client.ListAsync(500, cancellationToken: TestContext.Current.CancellationToken));

        TestContext.Current.TestOutputHelper?.WriteLine(ex.Message);
        ex.IsUnavailable.Should().BeFalse("the router answered; the client refused the payload");
        ex.InnerException.Should().BeOfType<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        ex.Message.Should().StartWith("Could not read persisted sessions: ");
    }

    /// <summary>
    /// A channel built the way <c>WasmRouterChannelProvider</c> builds the browser GUI's: a
    /// <see cref="GrpcWebHandler"/> in <see cref="GrpcWebMode.GrpcWeb"/> mode inside the channel's
    /// <see cref="HttpClient"/>, and no <see cref="GrpcChannelOptions.MaxReceiveMessageSize"/> override. Only
    /// the innermost handler differs. In place of the browser's fetch, it answers every call with
    /// <paramref name="response"/>, framed as gRPC-Web.
    /// </summary>
    private static GrpcChannel WasmShapedChannel(IMessage response)
    {
        var httpClient = new HttpClient(new GrpcWebHandler(mode: GrpcWebMode.GrpcWeb,
            innerHandler: new CannedGrpcWebResponder(response.ToByteArray())));
        return GrpcChannel.ForAddress(address: "http://localhost",
            channelOptions: new GrpcChannelOptions { HttpClient = httpClient, DisposeHttpClient = true });
    }

    /// <summary>
    /// A one-row response whose serialized size is exactly <paramref name="size"/> bytes. Between 2 MiB and
    /// 256 MiB the prompt's and the row's length prefixes are both four-byte varints, so each added ASCII
    /// character adds exactly one byte: pad once, then verify.
    /// </summary>
    private static Contract.ListPersistedSessionsResponse ResponseOfSerializedSize(int size)
    {
        var row = new Contract.PersistedTranscript
        {
            SessionId = "sess-big",
            CorrelationId = "sess-big:1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 9, 30, 12, 0, 0,
                offset: TimeSpan.Zero)),
            RequestedModel = "gpt-5.4",
            RoutedModel = "gpt-5.4",
            PromptText = new string('x', 3 * 1024 * 1024)
        };
        var response = new Contract.ListPersistedSessionsResponse { TranscriptCaptureEnabled = true };
        response.Transcripts.Add(row);

        row.PromptText = new string('x', row.PromptText.Length + size - response.CalculateSize());

        response.CalculateSize().Should().Be(size);
        return response;
    }

    /// <summary>
    /// Answers every request with one uncompressed gRPC-Web data frame carrying <paramref name="message"/>,
    /// then a trailers frame reporting OK.
    /// </summary>
    private sealed class CannedGrpcWebResponder(byte[] message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var trailers = "grpc-status:0\r\n"u8;
            var body = new byte[5 + message.Length + 5 + trailers.Length];
            WriteFrame(destination: body, flags: 0x00, payload: message);
            WriteFrame(destination: body.AsSpan(5 + message.Length), flags: 0x80, payload: trailers);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
            return Task.FromResult(response);
        }

        /// <summary>Writes a gRPC-Web frame: a flags byte, a big-endian 32-bit length, then the payload.</summary>
        private static void WriteFrame(Span<byte> destination, byte flags, ReadOnlySpan<byte> payload)
        {
            destination[0] = flags;
            BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)payload.Length);
            payload.CopyTo(destination[5..]);
        }
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
            CallOptions options)
        {
            return new AsyncUnaryCall<Contract.ListPersistedSessionsResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(Response)
                    : Task.FromException<Contract.ListPersistedSessionsResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}