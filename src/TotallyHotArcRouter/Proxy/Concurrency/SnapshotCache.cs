namespace TotallyHot.ArcRouter.Proxy.Concurrency;

/// <summary>
/// A lock-free-read cache for one immutable snapshot of <typeparamref name="T"/>, rebuilt and swapped as a
/// whole under a private gate. This is the shape <c>ProviderBudgetStore</c>, <c>PriceSourceToggleStore</c>,
/// and <c>ToolCallCapabilityStore</c> each hand-rolled independently before being consolidated onto this
/// type (docs/router/code-smell-refactoring-plan.md M2): a <see langword="volatile"/> field holding the
/// current snapshot, published by a single atomic reference assignment.
/// </summary>
/// <remarks>
/// <b>Invariant:</b> <see cref="Current"/> is lock-free and always observes a fully-formed snapshot, never a
/// torn or partially-built one. That holds because a snapshot is only ever replaced by reference - nothing
/// ever mutates an already-published <typeparamref name="T"/> in place - and because <see cref="Current"/>'s
/// backing field is <see langword="volatile"/>, giving a lock-free reader the acquire/release ordering it
/// needs to see the swap rather than a stale cached value.
/// <para>
/// This gate is private to the cache: it guards only the swap in <see cref="Rebuild"/> and is never shared
/// with a caller's own unrelated locking (e.g. a store that also guards a <see cref="Dictionary{TKey,TValue}"/>
/// of live <see cref="System.Threading.CancellationTokenSource"/> instances under its own gate) - callers must keep such
/// concerns on their own lock object rather than folding them into this one.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The immutable snapshot type. Must be a reference type so a reference-assignment swap is the only
/// operation <see cref="Rebuild"/> ever needs to publish a new snapshot atomically.
/// </typeparam>
internal sealed class SnapshotCache<T>
    where T : class
{
    private readonly object _gate = new();
    private volatile T _current;

    /// <summary>Initializes a new instance of the <see cref="SnapshotCache{T}"/> class with a starting snapshot.</summary>
    /// <param name="initial">
    /// The snapshot returned by <see cref="Current"/> until the first <see cref="Rebuild"/> call - typically
    /// an empty collection, matching every caller's own pre-<see cref="Rebuild"/> "nothing loaded yet"
    /// behavior.
    /// </param>
    public SnapshotCache(T initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Gets the current snapshot. Lock-free: never blocks on a concurrent <see cref="Rebuild"/>, and always
    /// returns either the previous snapshot or the new one in full - never a mix of the two.
    /// </summary>
    public T Current => _current;

    /// <summary>
    /// Builds a new snapshot and publishes it, replacing whatever <see cref="Current"/> returned before.
    /// </summary>
    /// <remarks>
    /// <paramref name="build"/> runs under this cache's gate, so two concurrent <see cref="Rebuild"/> calls
    /// are serialized rather than racing each other's build work - the same guarantee every hand-rolled
    /// predecessor of this type got from wrapping its own rebuild-then-swap in one <see langword="lock"/>
    /// block. A caller whose build step is expensive (a database round-trip, say) and wants concurrent
    /// rebuilds to overlap should build the candidate snapshot itself and swap it in via a thinner wrapper
    /// instead of putting the expensive work inside <paramref name="build"/>.
    /// </remarks>
    /// <param name="build">Produces the next snapshot from scratch. Its result becomes the new <see cref="Current"/>.</param>
    public void Rebuild(Func<T> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        lock (_gate)
        {
            _current = build();
        }
    }
}
