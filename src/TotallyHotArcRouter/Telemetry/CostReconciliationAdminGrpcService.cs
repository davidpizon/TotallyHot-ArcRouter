using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// gRPC service backing System Settings' Cost Reconciliation section: reports each configured provider's
/// reconciliation checkpoint and most recent reported-vs-local snapshot (§5.8), and runs a cycle on demand.
/// Mapped by <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/> onto the same loopback TLS endpoint as
/// <c>TelemetryService</c>.
/// </summary>
public sealed class CostReconciliationAdminGrpcService : Contract.CostReconciliationAdminService.CostReconciliationAdminServiceBase
{
    private readonly IReadOnlyList<IProviderCostReconciler> _reconcilers;
    private readonly CostReconciliationService _reconciliationService;
    private readonly IProviderCostReconciliationStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="CostReconciliationAdminGrpcService"/> class.
    /// </summary>
    /// <param name="store">The reconciliation checkpoint and snapshot history.</param>
    /// <param name="reconciliationService">Backs the panel's "Run Now" action.</param>
    /// <param name="reconcilers">
    /// Every provider with a resolvable Admin API key at the time this module was built - the set of
    /// providers this service reports on. A provider with no reconciler is not listed at all.
    /// </param>
    public CostReconciliationAdminGrpcService(
        IProviderCostReconciliationStore store,
        CostReconciliationService reconciliationService,
        IReadOnlyList<IProviderCostReconciler> reconcilers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reconciliationService);
        ArgumentNullException.ThrowIfNull(reconcilers);

        _store = store;
        _reconciliationService = reconciliationService;
        _reconcilers = reconcilers;
    }

    /// <inheritdoc/>
    public override Task<Contract.CostReconciliationStatusResponse> GetCostReconciliationStatus(
        Contract.GetCostReconciliationStatusRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(BuildResponse());
    }

    /// <inheritdoc/>
    public override async Task<Contract.CostReconciliationStatusResponse> RunCostReconciliationNow(
        Contract.RunCostReconciliationNowRequest request,
        ServerCallContext context)
    {
        await _reconciliationService.RunCycleAsync(context.CancellationToken).ConfigureAwait(false);
        return BuildResponse();
    }

    /// <summary>Builds the current status for every configured reconciler.</summary>
    private Contract.CostReconciliationStatusResponse BuildResponse()
    {
        var response = new Contract.CostReconciliationStatusResponse();

        foreach (var reconciler in _reconcilers)
        {
            var status = new Contract.ProviderReconciliationStatus { Provider = reconciler.Provider };

            var checkpoint = _store.GetLastReconciledDay(reconciler.Provider);
            if (checkpoint is { } day)
                status.LastReconciledDay = day.ToString(format: "yyyy-MM-dd", provider: CultureInfo.InvariantCulture);

            var latest = _store.GetLatestReconciliation(reconciler.Provider);
            if (latest is not null)
            {
                status.LastReportedCostUsd = latest.ProviderReportedCostUsd.ToString(CultureInfo.InvariantCulture);
                status.LastLocalCostUsd = latest.LocalEstimatedCostUsd.ToString(CultureInfo.InvariantCulture);
                status.LastFetchedAtUtc = Timestamp.FromDateTimeOffset(latest.FetchedAtUtc);
            }

            response.Providers.Add(status);
        }

        return response;
    }
}
