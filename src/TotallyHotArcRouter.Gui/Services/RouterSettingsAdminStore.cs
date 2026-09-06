using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's Adaptive Routing, Shadow Judge, and
/// Transcription Capture rows. Wraps
/// <see cref="RouterSettingsAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) with the same "singleton + Changed event + best-effort,
/// reachability-tolerant" shape as <see cref="ClusterModelAdminStore"/>, so the UI survives modal
/// close/reopen and degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class RouterSettingsAdminStore : IDisposable
{
    private readonly IRouterSettingsAdminClient _client;
    private readonly ILogger<RouterSettingsAdminStore>? _logger;
    private readonly IDisposable? _ownedClient;

    /// <summary>Initializes a new instance of the <see cref="RouterSettingsAdminStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public RouterSettingsAdminStore(
        ILogger<RouterSettingsAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new RouterSettingsAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    public RouterSettingsAdminStore(IRouterSettingsAdminClient client, ILogger<RouterSettingsAdminStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedClient = null;
        _logger = logger;
    }

    /// <summary>The router settings' last-known effective values, or <see langword="null"/> before the first load.</summary>
    public RouterSettingsInfo? Settings { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or save reached the proxy.</summary>
    /// <remarks>Same connectivity-only meaning as <see cref="ClusterModelAdminStore.IsReachable"/>.</remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach - or from the last rejection by - the router, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a save is currently in flight, so the UI can disable the Save button.</summary>
    public bool IsSaving { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ownedClient?.Dispose();
    }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the router settings' current effective values. Failures are swallowed and surfaced via
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than thrown, so the caller renders an error
    /// state instead of crashing when the proxy isn't running.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Settings = await _client.GetAsync(cancellationToken).ConfigureAwait(false);
            IsReachable = true;
            LastError = null;
        }
        catch (RouterSettingsAdminException ex)
        {
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load the router settings.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Validates and persists every setting, updating <see cref="Settings"/> to the fresh post-mutation
    /// effective values on success. The exception propagates on failure so the caller can render the
    /// specific outcome inline; <see cref="IsReachable"/>/<see cref="LastError"/> are still updated first.
    /// </summary>
    /// <param name="adaptiveRoutingEnabled">Whether adaptive routing is enabled.</param>
    /// <param name="embeddingMemoryCapacity">The embedding-memory capacity.</param>
    /// <param name="judgeEnabled">Whether the G-Eval shadow judge is enabled.</param>
    /// <param name="judgeModelName">The chosen judge backbone, or an empty string for automatic selection.</param>
    /// <param name="transcriptCaptureEnabled">Whether the opt-in transcript store captures raw prompt/response text.</param>
    /// <param name="codeJudgeEnabled">Whether Phase Q3's CodeJudge correctness grader is enabled.</param>
    /// <param name="iceScoreEnabled">Whether Phase Q3's ICE-Score usefulness grader is enabled.</param>
    /// <param name="raceEnabled">Whether Phase Q3's RACE readability/maintainability grader is enabled.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="RouterSettingsAdminException">The save was rejected or the router is unreachable.</exception>
    public async Task UpdateAsync(
        bool adaptiveRoutingEnabled,
        int embeddingMemoryCapacity,
        bool judgeEnabled,
        string judgeModelName,
        bool transcriptCaptureEnabled,
        bool codeJudgeEnabled = false,
        bool iceScoreEnabled = false,
        bool raceEnabled = false,
        CancellationToken cancellationToken = default)
    {
        IsSaving = true;
        Changed?.Invoke();

        try
        {
            Settings = await _client
                .UpdateAsync(adaptiveRoutingEnabled: adaptiveRoutingEnabled,
                    embeddingMemoryCapacity: embeddingMemoryCapacity, judgeEnabled: judgeEnabled,
                    judgeModelName: judgeModelName, transcriptCaptureEnabled: transcriptCaptureEnabled,
                    codeJudgeEnabled: codeJudgeEnabled, iceScoreEnabled: iceScoreEnabled, raceEnabled: raceEnabled,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            IsReachable = true;
            IsLoaded = true;
            LastError = null;
        }
        catch (RouterSettingsAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            IsSaving = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Deletes every captured transcript row - the Transcription Capture row's "Clear" action. Does not
    /// touch <see cref="Settings"/>; the toggle's own state is unaffected by clearing the data it captured.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    /// <exception cref="RouterSettingsAdminException">The call failed or the router is unreachable.</exception>
    public async Task<int> ClearTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        IsSaving = true;
        Changed?.Invoke();

        try
        {
            var rowsDeleted = await _client.ClearTranscriptsAsync(cancellationToken).ConfigureAwait(false);
            IsReachable = true;
            LastError = null;
            return rowsDeleted;
        }
        catch (RouterSettingsAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            IsSaving = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reflects a failed save in the store's state before the caller rethrows, so <see cref="IsReachable"/>
    /// keeps its documented meaning after a save and not only after a load.
    /// </summary>
    /// <remarks>
    /// Only a connectivity failure moves <see cref="IsReachable"/>. A rejection reached the router and is
    /// the caller's inline error to render; treating it as unreachable would misstate the cause.
    /// </remarks>
    private void RecordFailure(RouterSettingsAdminException ex)
    {
        LastError = ex.Message;
        if (!ex.IsUnavailable) return;

        IsReachable = false;
        _logger?.LogWarning(exception: ex, message: "The router became unreachable while saving the router settings.");
    }
}