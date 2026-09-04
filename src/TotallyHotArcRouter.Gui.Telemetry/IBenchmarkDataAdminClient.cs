namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The CodeRouterBench corpus management operations the Governance → Benchmark Data panel needs. An
/// interface so <c>BenchmarkDataStore</c> can be unit-tested against a fake without a live proxy or a
/// gRPC channel, mirroring <see cref="IPriceSourceAdminClient"/>.
/// </summary>
public interface IBenchmarkDataAdminClient
{
    /// <summary>Reads the router's last-computed corpus freshness state and every file's ledger status.</summary>
    /// <exception cref="BenchmarkDataAdminException">The call failed or the router is unreachable.</exception>
    Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-probes Hugging Face now and returns the recomputed freshness state.</summary>
    /// <exception cref="BenchmarkDataAdminException">The call failed or the router is unreachable.</exception>
    Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads, verifies, and imports every corpus file, yielding one <see cref="BenchmarkSyncEvent"/>
    /// per stage transition, plus one final event carrying the aggregate status once every file has been
    /// attempted.
    /// </summary>
    /// <exception cref="BenchmarkDataAdminException">The call failed or the router is unreachable.</exception>
    IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(CancellationToken cancellationToken = default);
}