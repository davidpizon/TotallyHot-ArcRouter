namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The regret-evaluation harness operations the Governance → Regret Harness panel needs. An interface so
/// <c>RegretHarnessAdminStore</c> can be unit-tested against a fake without a live proxy or a gRPC
/// channel, mirroring <see cref="ILogRegModelAdminClient"/>.
/// </summary>
public interface IRegretHarnessAdminClient
{
    /// <summary>Reads the last completed run's report, or the honest "no run yet this process" state.</summary>
    /// <exception cref="RegretHarnessAdminException">The call failed or the router is unreachable.</exception>
    Task<RegretHarnessStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the harness, yielding one <see cref="RegretHarnessRunEvent"/> per coarse stage-progress tick,
    /// plus one final event carrying the outcome.
    /// </summary>
    /// <exception cref="RegretHarnessAdminException">The call failed or the router is unreachable.</exception>
    IAsyncEnumerable<RegretHarnessRunEvent> RunAsync(CancellationToken cancellationToken = default);
}
