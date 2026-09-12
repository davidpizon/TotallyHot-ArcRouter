using System.Globalization;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// One configured provider's reconciliation state, as rendered by System Settings' Cost Reconciliation
/// section.
/// </summary>
/// <param name="Provider">The provider key (matches <c>ModelRouting:Providers</c>, e.g. <c>"openai"</c>).</param>
/// <param name="LastReconciledDay">The last fully-reconciled UTC day, or <see langword="null"/> if none yet.</param>
/// <param name="LastReportedCostUsd">
/// The most recent snapshot's provider-reported cost, or <see langword="null"/> if no snapshot has ever
/// been recorded.
/// </param>
/// <param name="LastLocalCostUsd">
/// The most recent snapshot's local estimated cost, absent exactly when <paramref name="LastReportedCostUsd"/> is.
/// </param>
/// <param name="LastFetchedAtUtc">When the most recent snapshot was fetched, absent alongside the two costs above.</param>
public sealed record ProviderReconciliationStatus(
    string Provider,
    DateOnly? LastReconciledDay,
    decimal? LastReportedCostUsd,
    decimal? LastLocalCostUsd,
    DateTimeOffset? LastFetchedAtUtc);

/// <summary>Client contract for <see cref="CostReconciliationAdminClient"/>. Enables a fake in store tests.</summary>
public interface ICostReconciliationAdminClient
{
    /// <summary>Reads every configured provider's current reconciliation status.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<IReadOnlyList<ProviderReconciliationStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a reconciliation cycle now and returns the resulting status.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<IReadOnlyList<ProviderReconciliationStatus>> RunNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>CostReconciliationAdminService</c> - System Settings' Cost Reconciliation
/// section's read and "Run Now" surface. Lives in this plain <c>net10.0</c> library rather than the
/// Windows-only MAUI project so CI can unit-test it, exactly like <see cref="RouterSettingsAdminClient"/>.
/// </summary>
public sealed class CostReconciliationAdminClient
    : GrpcAdminClientBase<Contract.CostReconciliationAdminService.CostReconciliationAdminServiceClient>,
        ICostReconciliationAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CostReconciliationAdminClient"/> class, creating and
    /// owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public CostReconciliationAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress: serverAddress,
            createClient: callInvoker =>
                new Contract.CostReconciliationAdminService.CostReconciliationAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CostReconciliationAdminClient"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server; the
    /// caller owns the channel's lifetime.
    /// </summary>
    public CostReconciliationAdminClient(
        Contract.CostReconciliationAdminService.CostReconciliationAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderReconciliationStatus>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetCostReconciliationStatusAsync(request: new Contract.GetCostReconciliationStatusRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Map(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the cost reconciliation status");
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderReconciliationStatus>> RunNowAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .RunCostReconciliationNowAsync(request: new Contract.RunCostReconciliationNowRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Map(response);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not run cost reconciliation");
        }
    }

    /// <summary>Converts a gRPC-contract response into the client's <see cref="ProviderReconciliationStatus"/> list.</summary>
    private static IReadOnlyList<ProviderReconciliationStatus> Map(Contract.CostReconciliationStatusResponse response)
    {
        return
        [
            .. response.Providers.Select(p => new ProviderReconciliationStatus(
                Provider: p.Provider,
                LastReconciledDay: p.HasLastReconciledDay
                    ? DateOnly.ParseExact(s: p.LastReconciledDay, format: "yyyy-MM-dd",
                        provider: CultureInfo.InvariantCulture)
                    : null,
                // Decimal-as-string, not double: see "Decimal encoding" in docs/router/grpc-migration.md.
                LastReportedCostUsd: p.HasLastReportedCostUsd
                    ? decimal.Parse(s: p.LastReportedCostUsd, provider: CultureInfo.InvariantCulture)
                    : null,
                LastLocalCostUsd: p.HasLastLocalCostUsd
                    ? decimal.Parse(s: p.LastLocalCostUsd, provider: CultureInfo.InvariantCulture)
                    : null,
                LastFetchedAtUtc: p.LastFetchedAtUtc?.ToDateTimeOffset()))
        ];
    }
}
