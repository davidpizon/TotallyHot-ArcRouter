using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// A running (sum, count) aggregate of every score observed for one (dimension, model) pair - the value
/// shape <see cref="RouterMemory"/> stores instead of the full <c>List&lt;double&gt;</c> of raw scores it
/// previously kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an aggregate rather than the raw scores.</b> <see cref="RouterMemory.GetAverageScore"/> was the
/// only consumer of the score list's contents, and it needs only the mean. Keeping every observation had
/// three compounding costs that this shape removes outright: the list grew without bound for the life of
/// the installation; <see cref="RouterMemory.GetAverageScore"/> recomputed an O(n) average on the routing
/// hot path, once per candidate per request; and persisting a score re-serialized the entire accumulated
/// history, because the store's contract took a whole-memory snapshot. A fixed-size aggregate bounds both
/// the in-memory structure and its <c>dimension_scores</c> table by the (dimension x model) vocabulary
/// rather than by observation count, and lets
/// <see cref="SqliteRouterMemoryStore.RecordScoreAsync"/> fold a new score in with one constant-cost
/// upsert - which is why no write debouncing or batching was needed.
/// </para>
/// <para>
/// <b>Immutability is load-bearing, not stylistic.</b> Updates replace the whole record via
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.AddOrUpdate{TArg}"/>, so a
/// concurrent serializer always observes either the previous aggregate or the next one, never a partially
/// updated value. The previous mutable <c>List&lt;double&gt;</c> had no such guarantee: it was appended
/// under a lock that <c>SaveAsync</c> did not take, so a save racing a score could throw or persist torn
/// output.
/// </para>
/// </remarks>
/// <param name="Sum">The sum of every score observed for the pair.</param>
/// <param name="Count">The number of scores observed for the pair.</param>
public sealed record ScoreAggregate(double Sum, int Count)
{
    /// <summary>
    /// Gets the mean of the observed scores, or <see langword="null"/> when nothing has been observed yet -
    /// matching <see cref="RouterMemory.GetAverageScore"/>'s "no data" contract, so a zero-count aggregate
    /// is never reported as a score of 0.
    /// </summary>
    /// <remarks>
    /// Ignored during serialization: it is derived from <see cref="Sum"/> and <see cref="Count"/>, and
    /// persisting it would both bloat the memory file and create a second, staleable source of truth.
    /// </remarks>
    [JsonIgnore]
    public double? Average => Count > 0 ? Sum / Count : null;

    /// <summary>Returns a new aggregate including <paramref name="score"/>, leaving this instance unchanged.</summary>
    /// <param name="score">The newly observed score to fold in.</param>
    /// <returns>The updated aggregate.</returns>
    public ScoreAggregate Add(double score) => new(Sum + score, Count + 1);
}
