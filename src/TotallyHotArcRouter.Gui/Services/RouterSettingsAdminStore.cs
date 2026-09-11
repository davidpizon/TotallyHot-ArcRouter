using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's Adaptive Routing, Shadow Judge, and
/// Transcription Capture rows. Wraps
/// <see cref="RouterSettingsAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the
/// UI survives modal close/reopen and degrades gracefully when the proxy isn't running. Registered in
/// <c>MauiProgram</c>.
/// </summary>
/// <remarks>
/// Passes <c>recordRejectionMessage</c> to <see cref="AdminStoreBase{TClient}.RecordFailure"/>: the System
/// Settings window reads <see cref="AdminStoreBase{TClient}.LastError"/> as its only error channel, so a
/// rejected save whose message never reached that property would look like it succeeded.
/// <see cref="ClusterModelAdminStore"/>, <see cref="LogRegModelAdminStore"/>, and
/// <see cref="RegretHarnessAdminStore"/> pass it too, for the same underlying reason (their own panel's
/// catch swallows the exception without capturing its message anywhere else) even though their UI reads it
/// as a fallback alongside a dedicated success-message field rather than as the only channel.
/// </remarks>
public sealed class RouterSettingsAdminStore : AdminStoreBase<IRouterSettingsAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminStore"/> class, creating and owning a
    /// client to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public RouterSettingsAdminStore(
        ILogger<RouterSettingsAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new RouterSettingsAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterSettingsAdminStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    /// <param name="client">The admin client to drive.</param>
    /// <param name="logger">Optional logger.</param>
    public RouterSettingsAdminStore(IRouterSettingsAdminClient client,
        ILogger<RouterSettingsAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>The router settings' last-known effective values, or <see langword="null"/> before the first load.</summary>
    public RouterSettingsInfo? Settings { get; private set; }

    /// <summary>Whether a save is currently in flight, so the UI can disable the Save button.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>
    /// Loads the router settings' current effective values. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the caller renders an error state instead of crashing when the proxy isn't
    /// running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Settings = await Client.GetAsync(ct).ConfigureAwait(false),
            "load the router settings",
            cancellationToken);
    }

    /// <summary>
    /// Validates and persists every setting, updating <see cref="Settings"/> to the fresh post-mutation
    /// effective values on success. The exception propagates on failure so the caller can render the
    /// specific outcome inline; <see cref="AdminStoreBase{TClient}.IsReachable"/> and
    /// <see cref="AdminStoreBase{TClient}.LastError"/> are still updated first.
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
    /// <exception cref="GrpcAdminException">The save was rejected or the router is unreachable.</exception>
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
        NotifyChanged();

        var savingCleared = false;

        try
        {
            Settings = await Client
                .UpdateAsync(adaptiveRoutingEnabled: adaptiveRoutingEnabled,
                    embeddingMemoryCapacity: embeddingMemoryCapacity, judgeEnabled: judgeEnabled,
                    judgeModelName: judgeModelName, transcriptCaptureEnabled: transcriptCaptureEnabled,
                    codeJudgeEnabled: codeJudgeEnabled, iceScoreEnabled: iceScoreEnabled, raceEnabled: raceEnabled,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "saving the router settings", recordRejectionMessage: true,
                beforeNotify: () =>
                {
                    IsSaving = false;
                    savingCleared = true;
                });
            throw;
        }
        finally
        {
            // RecordFailure's beforeNotify already cleared IsSaving and published the one notification a
            // failure gets; this is only the success path's, so it never double-notifies.
            if (!savingCleared)
            {
                IsSaving = false;
                NotifyChanged();
            }
        }
    }

    /// <summary>
    /// Deletes every captured transcript row - the Transcription Capture row's "Clear" action. Does not
    /// touch <see cref="Settings"/>; the toggle's own state is unaffected by clearing the data it captured.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    public async Task<int> ClearTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        IsSaving = true;
        NotifyChanged();

        var savingCleared = false;

        try
        {
            var rowsDeleted = await Client.ClearTranscriptsAsync(cancellationToken).ConfigureAwait(false);

            // marksLoaded: false - clearing transcripts fetches nothing to render, so it must not claim a
            // load has happened when none has.
            RecordSuccess(false);
            return rowsDeleted;
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "clearing the captured transcripts",
                recordRejectionMessage: true,
                beforeNotify: () =>
                {
                    IsSaving = false;
                    savingCleared = true;
                });
            throw;
        }
        finally
        {
            if (!savingCleared)
            {
                IsSaving = false;
                NotifyChanged();
            }
        }
    }
}
