using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// gRPC service backing the Governance → Routing Mode panel: a read-only report of whether the
/// Orchestrator ensemble (PLAN.md Phase L) is live, each voter's enablement and weight, and the
/// exploration setting. Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same
/// loopback TLS endpoint as <c>TelemetryService</c>.
/// </summary>
/// <remarks>
/// Read-only by design (docs/router/orchestrator-live-path-plan.md §M3.2): no RPC on this service
/// mutates <see cref="RoutingOptions"/>. Unlike the other admin services sharing this endpoint, this one
/// is always mapped rather than gated on an optional store being supplied - routing configuration is
/// core, bound at startup like any other <see cref="IOptions{TOptions}"/>, not an add-on feature that can
/// be absent.
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

    /// <inheritdoc />
    public override Task<Contract.RoutingModeResponse> GetRoutingMode(
        Contract.GetRoutingModeRequest request,
        ServerCallContext context)
    {
        var response = new Contract.RoutingModeResponse
        {
            OrchestratorEnabled = _options.EnableOrchestratorPolicy,
            ExplorationEnabled = _options.EnableExploration,
            ExplorationRate = _options.ExplorationRate,
        };

        response.Voters.Add(new Contract.VoterMode { Name = "dim_best", Enabled = _options.EnableDimBestVoter, Weight = _options.DimBestVoterWeight });
        response.Voters.Add(new Contract.VoterMode { Name = "memory_kNN", Enabled = _options.EnableMemoryKnnVoter, Weight = _options.MemoryKnnVoterWeight });
        response.Voters.Add(new Contract.VoterMode { Name = "logreg", Enabled = _options.EnableLogRegVoter, Weight = _options.LogRegVoterWeight });
        response.Voters.Add(new Contract.VoterMode { Name = "llm_router", Enabled = _options.EnableLlmRouterVoter, Weight = _options.LlmRouterVoterWeight });

        return Task.FromResult(response);
    }
}
