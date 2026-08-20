namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Counts proxy requests currently being served, so background analysis work can get out of the way
/// of live traffic. Exists for docs/router/routing-roi-regret-plan.md's non-interference guarantee:
/// <see cref="Transcripts.TaxonomyComparisonService"/> runs its comparison cycles only while this
/// gauge reads zero, and abandons a drain the moment a request arrives, so ROI computation can never
/// compete with the request path for CPU or SQLite access.
/// </summary>
/// <remarks>
/// A request counts as in-flight for the whole of <see cref="ProxyMiddleware.InvokeAsync"/> -
/// including the streaming of the response body - because that is exactly the window in which
/// background database work could add latency a client would feel. Registered as a singleton and
/// handed to both sides through optional constructor parameters, so tests and direct constructions
/// that pass nothing keep their existing behavior (no tracking, never paused).
/// </remarks>
public sealed class InFlightRequestGauge
{
    private int _count;

    /// <summary>
    /// Gets the number of proxy requests currently in flight. A read is a volatile snapshot: by the
    /// time the caller acts on it the number may have changed, which is fine for its one consumer -
    /// a pause check re-evaluated before every unit of background work.
    /// </summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Records that one request has started being served.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>
    /// Records that one request has finished (successfully or not). Every <see cref="Increment"/>
    /// must be balanced by exactly one call to this, or the gauge sticks above zero and pauses
    /// background work forever - which is why callers should prefer <see cref="Track"/>.
    /// </summary>
    public void Decrement() => Interlocked.Decrement(ref _count);

    /// <summary>
    /// Marks a request as in flight for the lifetime of the returned scope - the
    /// <c>using</c>-friendly form of an <see cref="Increment"/>/<see cref="Decrement"/> pair whose
    /// balance the compiler enforces on every exit path, including exceptions.
    /// </summary>
    /// <returns>A scope whose disposal ends the request's in-flight accounting.</returns>
    public IDisposable Track()
    {
        Increment();
        return new TrackingScope(this);
    }

    /// <summary>
    /// The scope <see cref="Track"/> hands out: decrements the owning gauge exactly once no matter
    /// how many times it is disposed, so a double <c>Dispose</c> cannot drive the count negative.
    /// </summary>
    private sealed class TrackingScope(InFlightRequestGauge owner) : IDisposable
    {
        private int _disposed;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Decrement();
            }
        }
    }
}
