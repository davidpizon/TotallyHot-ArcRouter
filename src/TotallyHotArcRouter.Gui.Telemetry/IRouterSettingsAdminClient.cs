namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The router-settings read/write operations the System Settings window's Adaptive Routing row needs. An
/// interface so <c>RouterSettingsAdminStore</c> can be unit-tested against a fake without a live proxy or a
/// gRPC channel, mirroring <see cref="IClusterModelAdminClient"/>.
/// </summary>
public interface IRouterSettingsAdminClient
{
    /// <summary>Reads the router settings' currently effective values.</summary>
    /// <exception cref="RouterSettingsAdminException">The call failed or the router is unreachable.</exception>
    Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and persists both settings, returning the fresh post-mutation effective values.</summary>
    /// <exception cref="RouterSettingsAdminException">The call was rejected (e.g. an out-of-range capacity) or the router is unreachable.</exception>
    Task<RouterSettingsInfo> UpdateAsync(
        bool adaptiveRoutingEnabled,
        int embeddingMemoryCapacity,
        CancellationToken cancellationToken = default);
}
