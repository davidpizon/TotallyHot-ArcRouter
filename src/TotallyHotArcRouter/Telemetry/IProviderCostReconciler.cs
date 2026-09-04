namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Fetches a provider's own reported spend for one closed UTC day, for comparison against the local
/// ledger estimate (§5.8). Only providers with a documented, stable, organization-level cost-reporting
/// API get an implementation - see <c>docs/router/agent-cost-tracking.md</c> §3.5 for which do and why.
/// </summary>
public interface IProviderCostReconciler
{
    /// <summary>The provider key this reconciler covers (matches <c>ModelRouting:Providers</c>, e.g. <c>"openai"</c>).</summary>
    string Provider { get; }

    /// <summary>
    /// Fetches the provider's own reported cost, in USD, for <paramref name="day"/> (a full UTC day),
    /// paginating to exhaustion and summing every page's contribution before returning.
    /// </summary>
    Task<decimal> GetReportedCostAsync(DateOnly day, CancellationToken cancellationToken = default);
}