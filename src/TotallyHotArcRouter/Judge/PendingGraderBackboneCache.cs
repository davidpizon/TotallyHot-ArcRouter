using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Additive wrapper over <see cref="PendingValueCache{T}"/>: up to four graders finish at different times
/// for the same request, each contributing one backbone model to the same correlation id's map, so
/// <see cref="Set"/> merges rather than replacing. <see cref="TryTake"/> still removes the accumulated
/// map because the aggregator writes exactly once per request.
/// </summary>
public sealed class PendingGraderBackboneCache
{
    private readonly PendingValueCache<Dictionary<string, string>> _inner;

    /// <summary>Initializes a new instance of the <see cref="PendingGraderBackboneCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL, shared with the other judge caches.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public PendingGraderBackboneCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new PendingValueCache<Dictionary<string, string>>(options, timeProvider);
    }

    /// <summary>Gets the number of correlation ids currently held (test/diagnostic use).</summary>
    internal int Count => _inner.Count;

    /// <summary>
    /// Records that <paramref name="graderKey"/> resolved <paramref name="backboneModel"/> for
    /// <paramref name="correlationId"/>, merging into any existing entry for the same correlation id.
    /// </summary>
    /// <param name="correlationId">The request correlation id the later observer will look up.</param>
    /// <param name="graderKey">Which grader resolved this backbone, e.g. <c>judge</c>.</param>
    /// <param name="backboneModel">The client-facing name of the model that actually scored.</param>
    public void Set(string correlationId, string graderKey, string backboneModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(backboneModel);

        _inner.Set(
            correlationId,
            value: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [graderKey] = backboneModel },
            merge: existing =>
            {
                existing[graderKey] = backboneModel;
                return existing;
            });
    }

    /// <summary>
    /// Removes and returns the accumulated grader-key-to-backbone-model map recorded under
    /// <paramref name="correlationId"/>. A miss returns an empty (never <see langword="null"/>) map.
    /// </summary>
    /// <param name="correlationId">The request correlation id to look up.</param>
    /// <param name="backboneByGraderKey">The accumulated map, or empty on a miss.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found and removed.</returns>
    public bool TryTake(string correlationId, out IReadOnlyDictionary<string, string> backboneByGraderKey)
    {
        if (_inner.TryTake(correlationId, out var map))
        {
            backboneByGraderKey = map;
            return true;
        }

        backboneByGraderKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return false;
    }
}
