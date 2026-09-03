namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The <c>logreg</c> voter management operations the Governance → Router Model panel needs. An interface
/// so <c>LogRegModelAdminStore</c> can be unit-tested against a fake without a live proxy or a gRPC
/// channel, mirroring <see cref="IClusterModelAdminClient"/>.
/// </summary>
public interface ILogRegModelAdminClient
{
    /// <summary>Reads the trained logreg model's current status plus the retrain threshold/live-sample-weight context.</summary>
    /// <exception cref="LogRegModelAdminException">The call failed or the router is unreachable.</exception>
    Task<LogRegModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a retrain, yielding one <see cref="LogRegRetrainEvent"/> per OOD bootstrap-embedding progress
    /// tick, plus one final event carrying the outcome and the fresh post-mutation status.
    /// </summary>
    /// <exception cref="LogRegModelAdminException">The call failed or the router is unreachable.</exception>
    IAsyncEnumerable<LogRegRetrainEvent> RetrainAsync(CancellationToken cancellationToken = default);
}