using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// gRPC service backing the Governance → Routing Mode panel: a read-only report of whether the
/// Orchestrator ensemble (PLAN.md Phase L) is live, each voter's enablement and weight, and the
/// exploration setting. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same
/// loopback TLS endpoint as <c>TelemetryService</c>.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by design (docs/router/orchestrator-live-path-plan.md §M3.2): no RPC on this service
/// mutates <see cref="RoutingOptions"/>. Unlike the other admin services sharing this endpoint, this one
/// is always mapped rather than gated on an optional store being supplied - routing configuration is
/// core, bound at startup like any other <see cref="IOptions{TOptions}"/>, not an add-on feature that can
/// be absent.
/// </para>
/// <para>
/// <b>Every voter name here comes from <see cref="VoterNames"/>, never a string literal.</b> This method
/// previously appended four hardcoded literals and was not updated when
/// docs/router/self-organizing-classification-plan.md Phase T3 added <c>cluster_best</c>, so the panel
/// silently reported four of five voters - the exact drift <see cref="VoterNames"/> exists to prevent.
/// Sourcing the names from that class makes the next added voter a visible omission here rather than a
/// silent one.
/// </para>
/// </remarks>
public sealed class RoutingModeAdminGrpcService : Contract.RoutingModeAdminService.RoutingModeAdminServiceBase
{
    private readonly RoutingOptions _options;

    /// <summary>Initializes a new instance of the <see cref="RoutingModeAdminGrpcService"/> class.</summary>
    public RoutingModeAdminGrpcService(IOptions<RoutingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc/>
    public override Task<Contract.RoutingModeResponse> GetRoutingMode(
        Contract.GetRoutingModeRequest request,
        ServerCallContext context)
    {
        var response = new Contract.RoutingModeResponse
        {
            OrchestratorEnabled = _options.EnableOrchestratorPolicy,
            ExplorationEnabled = _options.EnableExploration,
            ExplorationRate = _options.ExplorationRate
        };

        response.Voters.Add(new Contract.VoterMode
            { Name = VoterNames.DimBest, Enabled = _options.EnableDimBestVoter, Weight = _options.DimBestVoterWeight });
        response.Voters.Add(new Contract.VoterMode
        {
            Name = VoterNames.MemoryKnn, Enabled = _options.EnableMemoryKnnVoter, Weight = _options.MemoryKnnVoterWeight
        });
        response.Voters.Add(new Contract.VoterMode
            { Name = VoterNames.LogReg, Enabled = _options.EnableLogRegVoter, Weight = _options.LogRegVoterWeight });
        response.Voters.Add(new Contract.VoterMode
        {
            Name = VoterNames.LlmRouter, Enabled = _options.EnableLlmRouterVoter, Weight = _options.LlmRouterVoterWeight
        });

        // Reported like any other voter, deliberately un-gated on RoutingOptions.AdaptiveRoutingEnabled:
        // this panel reports *configuration*, and EnableClusterBestVoter is meaningful independently of
        // the adaptive-routing master switch. Hiding a configured voter would trade one inaccuracy for
        // another - the panel's own contract is "what would apply if the Orchestrator were live".
        response.Voters.Add(new Contract.VoterMode
        {
            Name = VoterNames.ClusterBest, Enabled = _options.EnableClusterBestVoter,
            Weight = _options.ClusterBestVoterWeight
        });

        return Task.FromResult(response);
    }
}