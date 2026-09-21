using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Model Distribution, Cost Analytics, and header-ticker surfaces' real
/// data (Phase 4, §5.15). Wraps <see cref="UsageQueryClient"/> with the same "singleton + best-effort,
/// reachability-tolerant" shape as <see cref="ProviderAdminStore"/>, so the UI survives tab switches and
/// degrades gracefully (falling back to demo data) when the proxy isn't running or has no rollup store
/// wired up. Registered as a singleton in the WASM host's <c>Program</c>.
/// </summary>
/// <remarks>
/// Unlike its sibling stores this one raises no <c>Changed</c> event. Every consumer awaits a load and
/// then renders from the returned value or from <see cref="Summary"/>, so there was never a subscriber;
/// the event was removed rather than left as a notification nobody listened to. A component that needs
/// push-style refresh should add it back deliberately, with the subscriber in the same change.
/// </remarks>
public sealed class UsageStore : IDisposable
{
    private readonly UsageQueryClient _client;
    private readonly ILogger<UsageStore>? _logger;

    // Keyed per distinct range/width/groupBy so repeated filter-bar clicks over an already-seen range don't
    // re-fetch. Small and unbounded by design: a session's worth of distinct filter selections is a handful
    // of entries, nowhere near large enough to need eviction. Concurrent, not a plain Dictionary: callers
    // (e.g. ModelDistribution's day/model pair) issue overlapping LoadRollupAsync calls via Task.WhenAll,
    // and a plain Dictionary's reads/writes aren't safe to interleave.
    private readonly ConcurrentDictionary<RollupCacheKey, IReadOnlyList<UsageRollupBucketView>> _rollupCache = new();

    /// <summary>Initializes a new instance of the <see cref="UsageStore"/> class.</summary>
    /// <param name="channelProvider">
    /// Supplies the shared call invoker this store's client is constructed over (web GUI migration plan
    /// Phase P5a) - see <see cref="TotallyHot.ArcRouter.Gui.Telemetry.IRouterChannelProvider"/>'s remarks.
    /// Required unless <paramref name="client"/> is supplied, in which case it is never consulted.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="adminToken">
    /// Optional management token; see <see cref="ProviderAdminStore"/>'s identical parameter for why the
    /// composition root resolves it, not this store.
    /// </param>
    /// <param name="client">
    /// A pre-built client to use instead of constructing one from <paramref name="channelProvider"/>; see
    /// <see cref="ProviderAdminStore"/>'s identical parameter for the full rationale.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="channelProvider"/> and <paramref name="client"/> are both null.</exception>
    public UsageStore(
        IRouterChannelProvider? channelProvider = null,
        ILogger<UsageStore>? logger = null,
        string? adminToken = null,
        UsageQueryClient? client = null)
    {
        _logger = logger;

        if (client is not null)
        {
            _client = client;
        }
        else
        {
            ArgumentNullException.ThrowIfNull(channelProvider);
            _client = new UsageQueryClient(channelProvider.CallInvoker, adminToken);
        }
    }

    /// <summary>
    /// The most recently loaded summary totals, or <see langword="null"/> until <see cref="LoadSummaryAsync"/>
    /// succeeds at least once.
    /// </summary>
    public UsageSummaryView? Summary { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load reached the proxy management API and a rollup store was available.</summary>
    public bool IsReachable { get; private set; }

    /// <summary>The last load error message, if the management API was unreachable or rollups are unavailable.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// No-op since Phase P5a: the shared channel behind <see cref="_client"/> is now owned by
    /// <see cref="TotallyHot.ArcRouter.Gui.Telemetry.IRouterChannelProvider"/>, not this store - see
    /// <see cref="ProviderAdminStore.Dispose"/>'s identical note. Kept implementing
    /// <see cref="IDisposable"/> so callers registered as a DI singleton in <c>MauiProgram</c> need no
    /// change, and in case this store takes on its own disposable state again later.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Loads totals for a preset window (the header ticker's System Tokens tile). Connection/unavailability
    /// failures are swallowed and surfaced via <see cref="IsReachable"/>/<see cref="LastError"/> rather than
    /// thrown, so the caller can fall back to demo data instead of crashing.
    /// </summary>
    public async Task LoadSummaryAsync(string window = "day", CancellationToken cancellationToken = default)
    {
        try
        {
            Summary = await _client.GetSummaryAsync(window: window, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            IsReachable = true;
            LastError = null;
        }
        catch (ProviderAdminException ex)
        {
            IsReachable = false;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load the usage summary from the management API.");
        }
        finally
        {
            IsLoaded = true;
        }
    }

    /// <summary>
    /// Loads (or returns the cached) chart feed for a range - the Model Distribution filter bar and Cost
    /// Analytics history call this on every selection change. Returns an empty list, rather than throwing,
    /// when the proxy is unreachable or rollups are unavailable; the caller falls back to demo data.
    /// </summary>
    public async Task<IReadOnlyList<UsageRollupBucketView>> LoadRollupAsync(
        DateTimeOffset from, DateTimeOffset to, string width, string groupBy,
        CancellationToken cancellationToken = default)
    {
        var key = new RollupCacheKey(From: from, To: to, Width: width, GroupBy: groupBy);
        if (_rollupCache.TryGetValue(key: key, value: out var cached)) return cached;

        try
        {
            var result = await _client
                .GetRollupAsync(from: from, to: to, width: width, groupBy: groupBy,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            _rollupCache[key] = result;
            IsReachable = true;
            LastError = null;
            return result;
        }
        catch (ProviderAdminException ex)
        {
            IsReachable = false;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load usage rollups from the management API.");
            return [];
        }
        finally
        {
            // Publishes nothing: unlike Summary, the result is handed straight back to the caller via the
            // awaited return value, so there is no shared state a listener would need to be notified
            // about. This is half of why the store carries no Changed event - see the type's remarks.
            IsLoaded = true;
        }
    }

    /// <summary>
    /// Loads the Cost Analytics "Routing ROI" feed for a range and optional session
    /// (docs/router/self-organizing-classification-plan.md Phase T4). Returns an empty list, rather than
    /// throwing, when the proxy is unreachable or no comparisons have been recorded yet.
    /// </summary>
    /// <param name="from">Inclusive lower bound on comparison time.</param>
    /// <param name="to">Exclusive upper bound.</param>
    /// <param name="sessionId">A session to filter to, or <see langword="null"/> for every session.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The matching points, or an empty list when unavailable.</returns>
    /// <remarks>
    /// Deliberately uncached, unlike <see cref="LoadRollupAsync"/>: the comparison job fills this table in
    /// the background, so the answer for a fixed range genuinely changes over time and the tab polls for
    /// it. Caching here would freeze the screen on whatever had been compared when it first rendered.
    /// </remarks>
    public async Task<IReadOnlyList<RoutingRoiPointView>> LoadRoutingRoiAsync(
        DateTimeOffset from, DateTimeOffset to, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client
                .GetRoutingRoiAsync(from: from, to: to, sessionId: sessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            IsReachable = true;
            return result;
        }
        catch (ProviderAdminException ex)
        {
            IsReachable = false;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex,
                message: "Failed to load routing ROI comparisons from the management API.");
            return [];
        }
        finally
        {
            IsLoaded = true;
        }
    }

    /// <summary>
    /// Cache key for <see cref="_rollupCache"/>: a rollup's full request shape, so distinct filter selections never
    /// collide.
    /// </summary>
    private readonly record struct RollupCacheKey(DateTimeOffset From, DateTimeOffset To, string Width, string GroupBy);
}