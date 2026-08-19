using TotallyHot.ArcRouter.Telemetry;
using Grpc.Core;
using Grpc.Core.Testing;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryGrpcService.StreamEvents"/>: registration with
/// <see cref="TelemetryBroadcaster"/>, forwarding published events to the response stream, and
/// unregistering when the call is cancelled. Unit-tested directly against a
/// <see cref="TestServerCallContext"/> and an in-memory <see cref="IServerStreamWriter{T}"/> fake
/// (see docs/router/grpc-migration.md's "Testing changes" - a full <c>TestHost</c>/<c>Grpc.Net.Client</c>
/// integration harness is heavier than this method's logic needs).
/// </summary>
public class TelemetryGrpcServiceTests
{
    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    private static ServerCallContext CreateContext(CancellationToken cancellationToken) =>
        TestServerCallContext.Create(
            method: "StreamEvents",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });

    private static RoutingTelemetryEvent SampleEvent() => new(
        SessionId: "sess-1",
        TurnNumber: 1,
        IsSessionSynthesized: false,
        RequestedModel: "gpt-5.4",
        ResolvedModel: "gpt-5.4",
        Provider: "openai",
        IsFallback: false,
        PromptTokens: 100,
        CompletionTokens: 20,
        EstimatedCostUsd: 0.001m,
        IsStreaming: false,
        LatencyToHeadersMs: 250,
        TotalDurationMs: 800,
        StatusCode: 200,
        TimestampUtc: DateTimeOffset.UtcNow,
        RoutedModel: "gpt-5.4");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    [Fact]
    public void Constructor_NullBroadcaster_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryGrpcService(null!));
    }

    [Fact]
    public async Task StreamEvents_DeliversPublishedEventsToResponseStream()
    {
        var broadcaster = new TelemetryBroadcaster();
        var service = new TelemetryGrpcService(broadcaster);
        var writer = new FakeServerStreamWriter<Contract.TelemetryEvent>();
        using var cts = new CancellationTokenSource();

        // StreamEvents registers with the broadcaster synchronously, before its first await (the
        // await foreach's initial MoveNextAsync on the not-yet-populated channel) - so by the time
        // this call returns a Task, registration has already happened and Publish below is safe.
        var callTask = service.StreamEvents(new Contract.StreamEventsRequest(), writer, CreateContext(cts.Token));

        var telemetryEvent = SampleEvent();
        broadcaster.Publish(telemetryEvent);

        await WaitUntilAsync(() => writer.Written.Count > 0, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(writer.Written);
        Assert.Equal(Contract.TelemetryEvent.EventOneofCase.RoutingTelemetry, writer.Written[0].EventCase);
        Assert.Equal(telemetryEvent.SessionId, writer.Written[0].RoutingTelemetry.SessionId);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callTask);
    }

    [Fact]
    public async Task StreamEvents_UnregistersFromBroadcasterWhenCallEnds()
    {
        var broadcaster = new TelemetryBroadcaster();
        var service = new TelemetryGrpcService(broadcaster);
        var writer = new FakeServerStreamWriter<Contract.TelemetryEvent>();
        using var cts = new CancellationTokenSource();

        var callTask = service.StreamEvents(new Contract.StreamEventsRequest(), writer, CreateContext(cts.Token));

        broadcaster.Publish(SampleEvent());
        await WaitUntilAsync(() => writer.Written.Count > 0, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callTask);

        // The call ended (finally { _broadcaster.Unregister(...) } ran), so a further publish must
        // not reach this call's now-abandoned writer.
        broadcaster.Publish(SampleEvent());
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Single(writer.Written);
    }
}

