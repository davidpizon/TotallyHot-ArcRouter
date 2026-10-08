using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Globalization;
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

    private ContentGrantStore? _contentGrant;

    // Serializes publishing a load's result with clearing, so a load that finishes after a lock cannot put
    // text back.
    private readonly object _publishGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionStore"/> class, over the shared
    /// <see cref="IRouterChannelProvider"/> every admin client and store talks through (web GUI
    /// migration plan Phase P5a) - see <see cref="IRouterChannelProvider"/>'s remarks.
    /// </summary>
    /// <param name="channelProvider">Supplies the shared call invoker this store's client is constructed over.</param>
    /// <param name="contentGrant">
    /// The content grant (ADR-0020), or <see langword="null"/> for a host with no passkey gate. When supplied,
    /// clearing it drops every cached prompt and response, and setting one reloads the list so the text the
    /// router now releases appears.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public PersistedSessionStore(
        IRouterChannelProvider channelProvider,
        ContentGrantStore? contentGrant = null,
        ILogger<PersistedSessionStore>? logger = null)
        : base(client: new PersistedSessionsClient(channelProvider.CallInvoker), logger: logger)
    {
        AttachContentGrant(contentGrant);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    /// <param name="client">The persisted-sessions client to read through.</param>
    /// <param name="contentGrant">See the other constructor's <c>contentGrant</c> parameter.</param>
    /// <param name="logger">Optional logger.</param>
    public PersistedSessionStore(
        IPersistedSessionsClient client,
        ContentGrantStore? contentGrant = null,
        ILogger<PersistedSessionStore>? logger = null)
        : base(client: client, logger: logger)
    {
        AttachContentGrant(contentGrant);
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
    /// Whether the last successful load left older persisted turns out - the router stops at its row limit
    /// and at a response byte budget that keeps the list under the gRPC client's receive cap (#179,
    /// ADR-0023). The Sessions tab says so rather than implying it shows the whole history.
    /// </summary>
    public bool HasMore { get; private set; }

    /// <summary>The number of persisted turns the last successful load returned, for the "newest N" notice.</summary>
    public int LoadedTurnCount { get; private set; }

    /// <summary>
    /// The one-line notice the Sessions tab shows about persisted history, or <see langword="null"/> when there
    /// is nothing to say. A failed load - an unreachable router or a rejected read - reports its error, since
    /// the tab would otherwise silently show live sessions only (#179). A successful load that left older turns
    /// out says how many it shows. Before the first load completes there is no notice.
    /// </summary>
    public string? HistoryNotice
    {
        get
        {
            if (!IsLoaded) return null;

            if (LastError is { } error) return $"Persisted history couldn't be loaded: {error}";

            return HasMore
                ? string.Create(provider: CultureInfo.InvariantCulture,
                    $"Showing the newest {LoadedTurnCount} persisted turns.")
                : null;
        }
    }

    /// <summary>
    /// Loads the most recent persisted sessions. Connection failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/> rather than thrown, so the Sessions tab
    /// renders whatever it already had (or an empty list, on first load) instead of crashing when the proxy
    /// isn't running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var unauthenticated = false;
        var loaded = await LoadGuardedAsync(
            async ct =>
            {
                PersistedSessionsResult result;
                var grantAtStart = _contentGrant?.Token;
                try
                {
                    result = await Client.ListAsync(limit: RequestLimit, cancellationToken: ct)
                        .ConfigureAwait(false);
                }
                catch (GrpcAdminException ex) when (ex.InnerException is RpcException
                                                    {
                                                        StatusCode: StatusCode.Unauthenticated
                                                    })
                {
                    unauthenticated = true;
                    throw;
                }

                lock (_publishGate)
                {
                    // The grant this request was sent under was cleared or replaced while it was in flight:
                    // its text-bearing response is stale and must not be shown while the dashboard is locked.
                    if (_contentGrant is not null &&
                        !string.Equals(_contentGrant.Token, grantAtStart, StringComparison.Ordinal))
                    {
                        return;
                    }

                    TranscriptCaptureEnabled = result.TranscriptCaptureEnabled;
                    HasMore = result.HasMore;
                    LoadedTurnCount = result.Transcripts.Count;
                    Sessions =
                    [
                        .. PersistedSessionAggregator.Aggregate(result.Transcripts).Select(PersistedSessionMapper.ToModel)
                    ];
                }
            },
            "load persisted sessions",
            cancellationToken).ConfigureAwait(false);

        // The router answered Unauthenticated while this dashboard believed it held a grant, so the grant is
        // no good any more (the router restarted, or another tab locked it). Fail closed: forget it, which
        // also clears every cached prompt and response.
        if (!loaded && unauthenticated && _contentGrant is { IsActive: true }) _contentGrant.Clear();
    }

    /// <summary>
    /// Drops every cached prompt and response from <see cref="Sessions"/> while keeping each session and turn's
    /// metadata (ids, models, tokens, costs, timestamps), so the Sessions tab can keep listing what happened
    /// after the content is locked (ADR-0020). Called when the content grant is cleared.
    /// </summary>
    public void ClearConversationText()
    {
        lock (_publishGate)
        {
            Sessions =
            [
                .. Sessions.Select(session => session with
                {
                    Turns = [.. session.Turns.Select(turn => turn with { RequestSummary = null, ResponseSummary = null })]
                })
            ];
        }

        NotifyChanged();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _contentGrant is not null)
        {
            _contentGrant.Cleared -= OnGrantCleared;
            _contentGrant.Granted -= OnGrantGranted;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Wires this store to <paramref name="contentGrant"/> so a lock drops cached text and an unlock reloads the
    /// list. Does nothing for a host without a passkey gate.
    /// </summary>
    private void AttachContentGrant(ContentGrantStore? contentGrant)
    {
        _contentGrant = contentGrant;
        if (contentGrant is null) return;

        contentGrant.Cleared += OnGrantCleared;
        contentGrant.Granted += OnGrantGranted;
    }

    /// <summary>Drops cached text when the grant is cleared, whatever cleared it.</summary>
    private void OnGrantCleared()
    {
        ClearConversationText();
    }

    /// <summary>Reloads the list when a grant is set, since the router now releases the text it withheld.</summary>
    private void OnGrantGranted()
    {
        _ = LoadAsync();
    }
}
