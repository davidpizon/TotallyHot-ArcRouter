using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// One voter's participation in the Orchestrator's weighted vote (PLAN.md Phase L), as rendered by the Governance
/// → Routing Mode panel.
/// </summary>
/// <param name="Name">The voter's name (<c>dim_best</c>, <c>memory_kNN</c>, <c>logreg</c>, or <c>llm_router</c>).</param>
/// <param name="Enabled">Whether the voter participates in the Orchestrator's vote.</param>
/// <param name="Weight">The voter's fixed weight in the weighted vote.</param>
public sealed record VoterMode(string Name, bool Enabled, double Weight);

/// <summary>
/// The routing configuration currently bound into the router's <c>RoutingOptions</c>, as read by the Governance →
/// Routing Mode panel.
/// </summary>
/// <param name="OrchestratorEnabled">Whether the Orchestrator ensemble is the live routing policy for non-utility traffic.</param>
/// <param name="ExplorationEnabled">Whether epsilon-greedy exploration is enabled.</param>
/// <param name="ExplorationRate">
/// The exploration rate used when <paramref name="ExplorationEnabled"/> is
/// <see langword="true"/>.
/// </param>
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
    : GrpcAdminClientBase<Contract.RoutingModeAdminService.RoutingModeAdminServiceClient>,
        IRoutingModeAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeAdminClient"/> class over a shared, already-
    /// authenticated call invoker (web GUI migration plan Phase P5a) - see
    /// <see cref="IRouterChannelProvider"/>'s remarks for why production goes through this shared
    /// invoker rather than a channel of its own. The
    /// caller owns the invoker's underlying channel.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public RoutingModeAdminClient(CallInvoker callInvoker)
        : base(new Contract.RoutingModeAdminService.RoutingModeAdminServiceClient(callInvoker))
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

    /// <inheritdoc/>
    public async Task<RoutingMode> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetRoutingModeAsync(request: new Contract.GetRoutingModeRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new RoutingMode(
                OrchestratorEnabled: response.OrchestratorEnabled,
                ExplorationEnabled: response.ExplorationEnabled,
                ExplorationRate: response.ExplorationRate,
                Voters:
                [
                    .. response.Voters.Select(v => new VoterMode(Name: v.Name, Enabled: v.Enabled, Weight: v.Weight))
                ]);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the routing mode");
        }
    }
}