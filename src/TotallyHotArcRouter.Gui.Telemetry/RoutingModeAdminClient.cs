using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a routing-mode read call fails. Carries a message fit to render in the Governance panel
/// rather than a raw <see cref="RpcException"/>, mirroring <see cref="PriceSourceAdminException"/>. See
/// <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class RoutingModeAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="RoutingModeAdminException"/> class.</summary>
    public RoutingModeAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException, isUnavailable)
    {
    }
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
public sealed class RoutingModeAdminClient
    : GrpcAdminClientBase<Contract.RoutingModeAdminService.RoutingModeAdminServiceClient, RoutingModeAdminException>,
      IRoutingModeAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RoutingModeAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress, callInvoker => new Contract.RoutingModeAdminService.RoutingModeAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public RoutingModeAdminClient(Contract.RoutingModeAdminService.RoutingModeAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc />
    public async Task<RoutingMode> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
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
            throw Wrap(ex, "Could not read the routing mode");
        }
    }

    /// <inheritdoc />
    protected override RoutingModeAdminException CreateException(string message, Exception? innerException, bool isUnavailable) =>
        new(message, innerException, isUnavailable);
}
