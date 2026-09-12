using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing System Settings' Cost Reconciliation section. Wraps
/// <see cref="CostReconciliationAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the
/// UI survives tab switches and degrades gracefully when the proxy isn't running. Registered in
/// <c>MauiProgram</c>.
/// </summary>
public sealed class CostReconciliationStore : AdminStoreBase<ICostReconciliationAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CostReconciliationStore"/> class, creating and owning a
    /// client to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public CostReconciliationStore(
        ILogger<CostReconciliationStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new CostReconciliationAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CostReconciliationStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    /// <param name="client">The admin client to drive.</param>
    /// <param name="logger">Optional logger.</param>
    public CostReconciliationStore(ICostReconciliationAdminClient client, ILogger<CostReconciliationStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>Every configured provider's reconciliation status, refreshed after each load or run.</summary>
    public IReadOnlyList<ProviderReconciliationStatus> Providers { get; private set; } = [];

    /// <summary>Whether a manual reconciliation cycle is currently running, so the UI can disable the button.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Loads the current reconciliation status. Connection failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the section renders an "unreachable" state instead of crashing when the proxy
    /// isn't running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Providers = await Client.GetStatusAsync(ct),
            "load the cost reconciliation status",
            cancellationToken);
    }

    /// <summary>
    /// Runs a reconciliation cycle now, waits for it, and publishes the resulting status.
    /// <see cref="IsRunning"/> is true for the duration.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="LoadAsync"/>, this rethrows: a load failing means "we couldn't show you this",
    /// which the unreachable state covers, but a run failing means "the thing you just asked for did not
    /// happen", which the operator has to be told inline. Same split as <see cref="PriceSourceStore"/>.
    /// </remarks>
    /// <exception cref="GrpcAdminException">The run could not be started or the router is unreachable.</exception>
    public async Task RunNowAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        NotifyChanged();

        var runningCleared = false;

        try
        {
            Providers = await Client.RunNowAsync(cancellationToken);
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a cost-reconciliation run",
                beforeNotify: () =>
                {
                    IsRunning = false;
                    runningCleared = true;
                });
            throw;
        }
        finally
        {
            // The exception still propagates for the section to render. RecordFailure's beforeNotify
            // already cleared IsRunning and published the failure's one notification, so this only runs
            // (and notifies) on the success path.
            if (!runningCleared)
            {
                IsRunning = false;
                NotifyChanged();
            }
        }
    }
}
