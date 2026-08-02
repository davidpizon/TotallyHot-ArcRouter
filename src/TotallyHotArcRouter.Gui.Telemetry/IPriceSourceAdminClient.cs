namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The price-source management operations the Governance → Price Sources panel needs. An interface so
/// <c>PriceSourceStore</c> can be unit-tested against a fake without a live proxy or a gRPC channel.
/// </summary>
public interface IPriceSourceAdminClient
{
    /// <summary>Reads every known source's status, enabled or not, and the schedule they poll on.</summary>
    /// <exception cref="PriceSourceAdminException">The call failed or the router is unreachable.</exception>
    Task<PriceSourceList> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables one source, returning every source's status afterward.
    /// </summary>
    /// <exception cref="PriceSourceAdminException">The call failed, or no such source exists.</exception>
    Task<PriceSourceList> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Runs an ingestion cycle now and waits for its result.</summary>
    /// <exception cref="PriceSourceAdminException">The call failed or the router is unreachable.</exception>
    Task<PriceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites every source's rank from <paramref name="namesInPriorityOrder"/>'s position (highest
    /// priority first) and runs an ingestion cycle so contested cells re-resolve under the new order
    /// immediately.
    /// </summary>
    /// <exception cref="PriceSourceAdminException">
    /// The call failed, the router is unreachable, or <paramref name="namesInPriorityOrder"/> did not name
    /// every existing source exactly once.
    /// </exception>
    Task<PriceRefreshResult> ReorderAsync(IReadOnlyList<string> namesInPriorityOrder, CancellationToken cancellationToken = default);
}

