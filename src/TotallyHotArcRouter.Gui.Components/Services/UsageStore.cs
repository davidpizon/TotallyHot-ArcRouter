using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Model Distribution, Cost Analytics, and header-ticker surfaces' real
/// data (Phase 4, §5.15). Wraps <see cref="UsageQueryClient"/> in the shared
/// <see cref="AdminStoreBase{TClient}"/> shape so the UI survives tab switches and degrades gracefully
/// (falling back to demo data) when the proxy isn't running or has no rollup store wired up. Registered as a
/// singleton in the WASM host's <c>Program</c>.
/// </summary>
public sealed class UsageStore : AdminStoreBase<UsageQueryClient>
{
    // Keyed per distinct range/width/groupBy so repeated filter-bar clicks over an already-seen range don't
    // re-fetch. Small and unbounded by design: a session's worth of distinct filter selections is a handful
    // of entries, nowhere near large enough to need eviction. Concurrent, not a plain Dictionary: callers
    // (e.g. ModelDistribution's day/model pair) issue overlapping LoadRollupAsync calls via Task.WhenAll,
    // and a plain Dictionary's reads/writes aren't safe to interleave.
    private readonly ConcurrentDictionary<RollupCacheKey, IReadOnlyList<UsageRollupBucketView>> _rollupCache = new();

    /// <summary>Initializes a new instance of the <see cref="UsageStore"/> class.</summary>
    /// <param name="channelProvider">
    /// Supplies the shared call invoker this store's client is constructed over. Required unless
    /// <paramref name="client"/> is supplied, in which case it is never consulted.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="client">
    /// A pre-built client to use instead of constructing one from <paramref name="channelProvider"/>; see
    /// <see cref="ProviderAdminStore"/>'s identical parameter for the full rationale.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="channelProvider"/> and <paramref name="client"/> are both null.</exception>
    public UsageStore(
        IRouterChannelProvider? channelProvider = null,
        ILogger<UsageStore>? logger = null,
        UsageQueryClient? client = null)
        : base(client: ResolveClient(channelProvider, client), logger: logger, ownsClient: client is null)
    {
    }

    /// <summary>
    /// Resolves the client this store drives: a caller-supplied instance, or one constructed over
    /// <paramref name="channelProvider"/>'s shared invoker.
    /// </summary>
    private static UsageQueryClient ResolveClient(IRouterChannelProvider? channelProvider, UsageQueryClient? client)
    {
        if (client is not null) return client;

        ArgumentNullException.ThrowIfNull(channelProvider);
        return new UsageQueryClient(channelProvider.CallInvoker);
    }

    /// <summary>
    /// The most recently loaded summary totals, or <see langword="null"/> until <see cref="LoadSummaryAsync"/>
    /// succeeds at least once.
    /// </summary>
    public UsageSummaryView? Summary { get; private set; }

    /// <summary>
    /// Loads totals for a preset window (the header ticker's System Tokens tile). Connection/unavailability
    /// failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the caller can fall back to demo data instead of crashing.
    /// </summary>
    /// <param name="window">One of <c>"day"</c>, <c>"week"</c>, <c>"month"</c>, or <c>"all"</c>.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadSummaryAsync(string window = "day", CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Summary = await Client.GetSummaryAsync(window: window, cancellationToken: ct)
                .ConfigureAwait(false),
            "read the usage summary",
            cancellationToken);
    }

    /// <summary>
    /// Loads (or returns the cached) chart feed for a range - the Model Distribution filter bar and Cost
    /// Analytics history call this on every selection change. Returns an empty list, rather than throwing,
    /// when the proxy is unreachable or rollups are unavailable; the caller falls back to demo data.
    /// </summary>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Exclusive range end.</param>
    /// <param name="width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="groupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The matching buckets, or an empty list when unavailable.</returns>
    /// <remarks>
    /// Uses <see cref="AdminStoreBase{TClient}.LoadGuardedAsync"/>, which raises <c>Changed</c>. No current
    /// subscriber re-fetches from that event with a fresh <c>DateTimeOffset.UtcNow</c> range — callers
    /// await this method's return value instead.
    /// </remarks>
    public async Task<IReadOnlyList<UsageRollupBucketView>> LoadRollupAsync(
        DateTimeOffset from, DateTimeOffset to, string width, string groupBy,
        CancellationToken cancellationToken = default)
    {
        var key = new RollupCacheKey(From: from, To: to, Width: width, GroupBy: groupBy);
        if (_rollupCache.TryGetValue(key: key, value: out var cached)) return cached;

        IReadOnlyList<UsageRollupBucketView> result = [];
        await LoadGuardedAsync(
            async ct =>
            {
                result = await Client
                    .GetRollupAsync(from: from, to: to, width: width, groupBy: groupBy,
                        cancellationToken: ct).ConfigureAwait(false);
                _rollupCache[key] = result;
            },
            "read usage rollups",
            cancellationToken).ConfigureAwait(false);
        return result;
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
        IReadOnlyList<RoutingRoiPointView> result = [];
        await LoadGuardedAsync(
            async ct => result = await Client
                .GetRoutingRoiAsync(from: from, to: to, sessionId: sessionId, cancellationToken: ct)
                .ConfigureAwait(false),
            "read routing ROI comparisons",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Cache key for <see cref="_rollupCache"/>: a rollup's full request shape, so distinct filter selections never
    /// collide.
    /// </summary>
    /// <param name="From">Inclusive range start.</param>
    /// <param name="To">Exclusive range end.</param>
    /// <param name="Width">Bucket width: <c>"hour"</c> or <c>"day"</c>.</param>
    /// <param name="GroupBy"><c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>.</param>
    private readonly record struct RollupCacheKey(DateTimeOffset From, DateTimeOffset To, string Width, string GroupBy);
}
