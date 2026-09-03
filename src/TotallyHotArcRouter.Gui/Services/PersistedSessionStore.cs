using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Sessions tab's persisted-history view
/// (docs/router/sessions-tab-training-data-plan.md Phase 2). Wraps <see cref="IPersistedSessionsClient"/>
/// with the same "singleton + Changed event + best-effort, reachability-tolerant" shape as
/// <see cref="RoutingModeStore"/>, so the tab degrades gracefully when the router isn't running or
/// transcript capture is off. Registered in <c>MauiProgram</c>.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="LiveDataStore"/> rather than folding persisted sessions into its
/// <see cref="LiveDataStore.Conversations"/>: that list also backs the Cost Analytics tab, which has no use
/// for persisted-history sessions or the <see cref="Conversation.IsUsedForTraining"/> concept, and merging
/// there would mean every consumer of live data has to reason about the persisted-history merge. Only
/// <c>LiveStream.razor</c> (the Sessions tab) merges the two lists, live winning for any session id present
/// in both - see its own remarks.
/// </remarks>
public sealed class PersistedSessionStore : IDisposable
{
    /// <summary>
    /// The maximum number of transcript rows requested per load. Bounded rather than unbounded: this is
    /// a GUI history view, not an export tool, and a very large transcript store would otherwise make
    /// every tab-open a slow, memory-heavy query.
    /// </summary>
    private const int RequestLimit = 500;

    private readonly IPersistedSessionsClient _client;
    private readonly ILogger<PersistedSessionStore>? _logger;
    private readonly IDisposable? _ownedClient;

    /// <summary>Initializes a new instance of the <see cref="PersistedSessionStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public PersistedSessionStore(
        ILogger<PersistedSessionStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new PersistedSessionsClient(serverAddress);
        _client = client;
        _ownedClient = client;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    public PersistedSessionStore(IPersistedSessionsClient client, ILogger<PersistedSessionStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedClient = null;
        _logger = logger;
    }

    /// <summary>Persisted sessions as of the last successful load, oldest first. Empty before the first load.</summary>
    public IReadOnlyList<Conversation> Sessions { get; private set; } = [];

    /// <summary>
    /// Whether transcript capture was enabled as of the last successful load. <see langword="false"/>
    /// means <see cref="Sessions"/> is empty because capture is off, not because no traffic has been
    /// persisted yet - the Sessions tab renders these two states differently.
    /// </summary>
    public bool TranscriptCaptureEnabled { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load reached the proxy.</summary>
    public bool IsReachable { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ownedClient?.Dispose();
    }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the most recent persisted sessions. Connection failures are swallowed and surfaced via
    /// <see cref="IsReachable"/> rather than thrown, so the Sessions tab renders whatever it already had
    /// (or an empty list, on first load) instead of crashing when the proxy isn't running.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.ListAsync(limit: RequestLimit, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            TranscriptCaptureEnabled = result.TranscriptCaptureEnabled;
            Sessions = PersistedSessionAggregator.Aggregate(result.Transcripts)
                .Select(PersistedSessionMapper.ToModel)
                .ToList();
            IsReachable = true;
        }
        catch (PersistedSessionsClientException ex)
        {
            IsReachable = false;
            _logger?.LogWarning(exception: ex, message: "Failed to load persisted sessions from the router.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }
}