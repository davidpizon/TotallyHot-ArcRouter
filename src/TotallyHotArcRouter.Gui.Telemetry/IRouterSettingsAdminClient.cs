namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The router-settings read/write operations the System Settings window's Adaptive Routing, Shadow Judge,
/// and Transcription Capture rows need. An interface so <c>RouterSettingsAdminStore</c> can be
/// unit-tested against a fake without a live proxy or a gRPC channel, mirroring <see cref="IClusterModelAdminClient"/>.
/// </summary>
public interface IRouterSettingsAdminClient
{
    /// <summary>Reads the router settings' currently effective values.</summary>
    /// <exception cref="RouterSettingsAdminException">The call failed or the router is unreachable.</exception>
    Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and persists every setting, returning the fresh post-mutation effective values.</summary>
    /// <param name="adaptiveRoutingEnabled">Whether adaptive routing is enabled.</param>
    /// <param name="embeddingMemoryCapacity">The embedding-memory capacity; rejected when out of range.</param>
    /// <param name="judgeEnabled">Whether the G-Eval shadow judge is enabled.</param>
    /// <param name="judgeModelName">The chosen judge backbone, or an empty string for automatic selection; rejected when it names a model that is not currently eligible.</param>
    /// <param name="transcriptCaptureEnabled">Whether the opt-in transcript store captures raw prompt/response text.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="RouterSettingsAdminException">The call was rejected (e.g. an out-of-range capacity, or an ineligible judge model) or the router is unreachable.</exception>
    Task<RouterSettingsInfo> UpdateAsync(
        bool adaptiveRoutingEnabled,
        int embeddingMemoryCapacity,
        bool judgeEnabled,
        string judgeModelName,
        bool transcriptCaptureEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every captured transcript row - the Transcription Capture row's "Clear" action.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    /// <exception cref="RouterSettingsAdminException">The call failed or the router is unreachable.</exception>
    Task<int> ClearTranscriptsAsync(CancellationToken cancellationToken = default);
}
