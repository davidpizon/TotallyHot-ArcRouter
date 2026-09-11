using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Sessions tab's persisted-history view
/// (docs/router/sessions-tab-training-data-plan.md Phase 2). Wraps <see cref="IPersistedSessionsClient"/>
/// in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the tab degrades gracefully when the
/// router isn't running or transcript capture is off. Registered in <c>MauiProgram</c>.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="LiveDataStore"/> rather than folding persisted sessions into its
/// <see cref="LiveDataStore.Conversations"/>: that list also backs the Cost Analytics tab, which has no use
/// for persisted-history sessions or the <see cref="Conversation.IsUsedForTraining"/> concept, and merging
/// there would mean every consumer of live data has to reason about the persisted-history merge. Only
/// <c>LiveStream.razor</c> (the Sessions tab) merges the two lists, live winning for any session id present
/// in both - see its own remarks.
/// </remarks>
public sealed class PersistedSessionStore : AdminStoreBase<IPersistedSessionsClient>
{
    /// <summary>
    /// The maximum number of transcript rows requested per load. Bounded rather than unbounded: this is
    /// a GUI history view, not an export tool, and a very large transcript store would otherwise make
    /// every tab-open a slow, memory-heavy query.
    /// </summary>
    private const int RequestLimit = 500;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionStore"/> class, creating and owning a
    /// client to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public PersistedSessionStore(
        ILogger<PersistedSessionStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new PersistedSessionsClient(serverAddress), logger: logger, ownsClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    /// <param name="client">The persisted-sessions client to read through.</param>
    /// <param name="logger">Optional logger.</param>
    public PersistedSessionStore(IPersistedSessionsClient client, ILogger<PersistedSessionStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>Persisted sessions as of the last successful load, oldest first. Empty before the first load.</summary>
    public IReadOnlyList<Conversation> Sessions { get; private set; } = [];

    /// <summary>
    /// Whether transcript capture was enabled as of the last successful load. <see langword="false"/>
    /// means <see cref="Sessions"/> is empty because capture is off, not because no traffic has been
    /// persisted yet - the Sessions tab renders these two states differently.
    /// </summary>
    public bool TranscriptCaptureEnabled { get; private set; }

    /// <summary>
    /// Loads the most recent persisted sessions. Connection failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/> rather than thrown, so the Sessions tab
    /// renders whatever it already had (or an empty list, on first load) instead of crashing when the proxy
    /// isn't running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct =>
            {
                var result = await Client.ListAsync(limit: RequestLimit, cancellationToken: ct)
                    .ConfigureAwait(false);
                TranscriptCaptureEnabled = result.TranscriptCaptureEnabled;
                Sessions =
                [
                    .. PersistedSessionAggregator.Aggregate(result.Transcripts).Select(PersistedSessionMapper.ToModel)
                ];
            },
            "load persisted sessions",
            cancellationToken);
    }
}
