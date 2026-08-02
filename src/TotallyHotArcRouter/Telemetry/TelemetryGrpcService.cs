using System.Threading.Channels;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// gRPC service dashboards connect to for live routing telemetry and log lines. Replaces the former
/// SignalR <c>TelemetryHub</c> - see docs/router/grpc-migration.md. Mapped at the
/// <c>TelemetryService.StreamEvents</c> RPC by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>.
/// </summary>
public sealed class TelemetryGrpcService : Contract.TelemetryService.TelemetryServiceBase
{
    private readonly TelemetryBroadcaster _broadcaster;

    /// <param name="broadcaster">Registers/unregisters each call's channel writer and receives published events.</param>
    public TelemetryGrpcService(TelemetryBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// Per-call channel capacity. Bounded (not unbounded) so a stalled or unusually slow client
    /// can't make its buffered backlog grow without limit while <see cref="TelemetryBroadcaster"/>
    /// keeps publishing - telemetry is explicitly best-effort (see
    /// <see cref="ITelemetryPublisher.PublishAsync"/>'s contract), so <see cref="BoundedChannelFullMode.DropOldest"/>
    /// discards the stalest buffered event to make room rather than blocking the publisher or
    /// growing memory - a live dashboard cares about catching up to *current* state, not replaying
    /// every dropped event once a slow client catches up.
    /// </summary>
    private const int ChannelCapacity = 1024;

    /// <summary>
    /// Streams every event <see cref="TelemetryBroadcaster"/> publishes to this one connected client,
    /// for the lifetime of the call - the gRPC equivalent of SignalR's push-only hub connection.
    /// gRPC server-streaming has no built-in "broadcast to every call" primitive the way
    /// <c>IHubContext.Clients.All</c> did, so each call registers its own bounded
    /// <see cref="Channel{T}"/> with the shared <see cref="TelemetryBroadcaster"/> for its duration.
    /// </summary>
    public override async Task StreamEvents(
        Contract.StreamEventsRequest request,
        IServerStreamWriter<Contract.TelemetryEvent> responseStream,
        ServerCallContext context)
    {
        var channel = Channel.CreateBounded<Contract.TelemetryEvent>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
        _broadcaster.Register(channel.Writer);
        try
        {
            await foreach (var telemetryEvent in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(telemetryEvent);
            }
        }
        finally
        {
            _broadcaster.Unregister(channel.Writer);
        }
    }
}

