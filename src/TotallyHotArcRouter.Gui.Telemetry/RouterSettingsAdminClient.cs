using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a router-settings read or write call fails. Carries a message fit to render in the System
/// Settings window rather than a raw <see cref="RpcException"/>, mirroring <see cref="ClusterModelAdminException"/>.
/// </summary>
public sealed class RouterSettingsAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RouterSettingsAdminException"/> class.</summary>
    public RouterSettingsAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>
    /// Gets whether the call failed because the router could not be reached, as opposed to being rejected by
    /// it (e.g. an out-of-range capacity). See <see cref="ClusterModelAdminException.IsUnavailable"/>'s
    /// remarks for why this distinction is load-bearing for the caller.
    /// </summary>
    public bool IsUnavailable { get; }
}

/// <summary>
/// The router settings' currently effective values (docs/router/self-organizing-classification-plan.md
/// Phase T6), as read or written by the System Settings window's Adaptive Routing row.
/// </summary>
/// <param name="AdaptiveRoutingEnabled">Whether adaptive routing's transcript capture, cluster retraining, and <c>cluster_best</c> voter are enabled.</param>
/// <param name="EmbeddingMemoryCapacity">The maximum number of embedding-memory entries retained before oldest-first eviction.</param>
public sealed record RouterSettingsInfo(bool AdaptiveRoutingEnabled, int EmbeddingMemoryCapacity);

/// <summary>
/// Client for the proxy's <c>RouterSettingsAdminService</c> - the System Settings window's Adaptive Routing
/// read and write surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI
/// project so CI can unit-test it, exactly like <see cref="ClusterModelAdminClient"/>.
/// </summary>
public sealed class RouterSettingsAdminClient : IRouterSettingsAdminClient, IDisposable
{
    private readonly Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RouterSettingsAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public RouterSettingsAdminClient(Contract.RouterSettingsAdminService.RouterSettingsAdminServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetRouterSettingsAsync(new Contract.GetRouterSettingsRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Map(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not read the router settings");
        }
    }

    /// <inheritdoc />
    public async Task<RouterSettingsInfo> UpdateAsync(
        bool adaptiveRoutingEnabled,
        int embeddingMemoryCapacity,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .UpdateRouterSettingsAsync(
                    new Contract.UpdateRouterSettingsRequest
                    {
                        AdaptiveRoutingEnabled = adaptiveRoutingEnabled,
                        EmbeddingMemoryCapacity = embeddingMemoryCapacity,
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Map(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not save the router settings");
        }
    }

    /// <summary>Converts a gRPC-contract response into the client's <see cref="RouterSettingsInfo"/>.</summary>
    private static RouterSettingsInfo Map(Contract.RouterSettingsResponse response) =>
        new(response.AdaptiveRoutingEnabled, response.EmbeddingMemoryCapacity);

    // Unavailable means the proxy isn't running - an ordinary state for a GUI that can outlive it - so it
    // gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can tell a
    // dead connection from a rejected request (e.g. an out-of-range capacity) without parsing the text.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="RouterSettingsAdminException"/>, flagging router-unreachable errors distinctly.</summary>
    private static RouterSettingsAdminException Wrap(RpcException ex, string action) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new RouterSettingsAdminException($"{action}: the router is not reachable.", ex, isUnavailable: true)
            : new RouterSettingsAdminException($"{action}: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}
