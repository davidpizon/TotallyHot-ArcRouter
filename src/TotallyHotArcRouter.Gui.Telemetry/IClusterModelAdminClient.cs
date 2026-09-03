namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The self-organizing cluster model management operations the Governance → Cluster Model panel needs. An
/// interface so <c>ClusterModelAdminStore</c> can be unit-tested against a fake without a live proxy or a
/// gRPC channel, mirroring <see cref="IBenchmarkDataAdminClient"/>.
/// </summary>
public interface IClusterModelAdminClient
{
    /// <summary>Reads the trained cluster model's current status plus the transcript retention context.</summary>
    /// <exception cref="ClusterModelAdminException">The call failed or the router is unreachable.</exception>
    Task<ClusterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a retrain, yielding one <see cref="ClusterRetrainEvent"/> per OOD bootstrap-embedding progress
    /// tick, plus one final event carrying the outcome and the fresh post-mutation status.
    /// </summary>
    /// <exception cref="ClusterModelAdminException">The call failed or the router is unreachable.</exception>
    IAsyncEnumerable<ClusterRetrainEvent> RetrainAsync(CancellationToken cancellationToken = default);
}