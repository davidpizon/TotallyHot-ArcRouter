using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a routing-mode read call fails. Carries a message fit to render in the Governance panel
/// rather than a raw <see cref="RpcException"/>, mirroring <see cref="PriceSourceAdminException"/>.
/// </summary>
public sealed class RoutingModeAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RoutingModeAdminException"/> class.</summary>
    public RoutingModeAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>Gets whether the call failed because the router could not be reached.</summary>
    public bool IsUnavailable { get; }
}

/// <summary>One voter's participation in the Orchestrator's weighted vote (PLAN.md Phase L), as rendered by the Governance → Routing Mode panel.</summary>
/// <param name="Name">The voter's name (<c>dim_best</c>, <c>memory_kNN</c>, <c>logreg</c>, or <c>llm_router</c>).</param>
/// <param name="Enabled">Whether the voter participates in the Orchestrator's vote.</param>
/// <param name="Weight">The voter's fixed weight in the weighted vote.</param>
public sealed record VoterMode(string Name, bool Enabled, double Weight);

/// <summary>The routing configuration currently bound into the router's <c>RoutingOptions</c>, as read by the Governance → Routing Mode panel.</summary>
/// <param name="OrchestratorEnabled">Whether the Orchestrator ensemble is the live routing policy for non-utility traffic.</param>
/// <param name="ExplorationEnabled">Whether epsilon-greedy exploration is enabled.</param>
/// <param name="ExplorationRate">The exploration rate used when <paramref name="ExplorationEnabled"/> is <see langword="true"/>.</param>
/// <param name="Voters">Every voter's enablement and weight, in the order the router reported them.</param>
public sealed record RoutingMode(
    bool OrchestratorEnabled,
    bool ExplorationEnabled,
    double ExplorationRate,
    IReadOnlyList<VoterMode> Voters);

/// <summary>
/// Client for the proxy's <c>RoutingModeAdminService</c> - the Governance → Routing Mode panel's read-only
/// surface. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so CI can
/// unit-test it, exactly like <c>PriceSourceAdminClient</c>.
/// </summary>
public sealed class RoutingModeAdminClient : IRoutingModeAdminClient, IDisposable
{
    private readonly Contract.RoutingModeAdminService.RoutingModeAdminServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RoutingModeAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.RoutingModeAdminService.RoutingModeAdminServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public RoutingModeAdminClient(Contract.RoutingModeAdminService.RoutingModeAdminServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<RoutingMode> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetRoutingModeAsync(new Contract.GetRoutingModeRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new RoutingMode(
                response.OrchestratorEnabled,
                response.ExplorationEnabled,
                response.ExplorationRate,
                [.. response.Voters.Select(v => new VoterMode(v.Name, v.Enabled, v.Weight))]);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex);
        }
    }

    // Unavailable means the proxy isn't running, which is an ordinary state for a GUI that can outlive it -
    // so it gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can
    // tell a dead connection from a rejected request without parsing the text. Mirrors PriceSourceAdminClient's Wrap.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="RoutingModeAdminException"/>, flagging router-unreachable errors distinctly.</summary>
    private static RoutingModeAdminException Wrap(RpcException ex) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new RoutingModeAdminException("Could not read the routing mode: the router is not reachable.", ex, isUnavailable: true)
            : new RoutingModeAdminException($"Could not read the routing mode: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}
