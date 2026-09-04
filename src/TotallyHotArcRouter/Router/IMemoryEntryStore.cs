namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Persists <see cref="MemoryEntry"/> rows for PLAN.md Phase J's task-embedding-keyed memory. Owns
/// storage only - retrieval (cosine kNN) and FIFO eviction policy live in <see cref="EmbeddingMemory"/>,
/// mirroring the <see cref="IRouterMemoryStore"/>/<see cref="RouterMemory"/> split already used for the
/// dimension-hashed memory.
/// </summary>
public interface IMemoryEntryStore
{
    /// <summary>
    /// Loads every persisted entry, oldest first.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new entry and returns it with its store-assigned <see cref="MemoryEntry.Id"/>.
    /// </summary>
    /// <param name="entry">The entry to persist. Its <see cref="MemoryEntry.Id"/> is ignored.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a previously persisted entry by id, used to enforce the FIFO 20,000-entry bound.
    /// </summary>
    /// <param name="id">The entry's store-assigned id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the highest store-assigned <see cref="MemoryEntry.Id"/>, or <c>0</c> when the store is
    /// empty - a cheap change stamp for callers that cache a full <see cref="LoadAllAsync"/> snapshot
    /// (docs/router/routing-roi-regret-plan.md's ledger cache). Every append advances it, and FIFO
    /// eviction only ever happens alongside an append, so an unchanged value means the snapshot is
    /// current. Chosen over a row count precisely because the count plateaus at the FIFO bound while
    /// the content keeps changing.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <remarks>
    /// The default implementation loads everything and takes the maximum - correct but no cheaper than
    /// the snapshot it is meant to guard, so real (persistent) stores should override it with a native
    /// aggregate query, as <see cref="SqliteMemoryEntryStore"/> does. It exists as a default so the
    /// many in-memory test fakes need no change.
    /// </remarks>
    async Task<long> GetMaxIdAsync(CancellationToken cancellationToken = default)
    {
        var entries = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return entries.Count == 0 ? 0 : entries.Max(e => e.Id);
    }
}