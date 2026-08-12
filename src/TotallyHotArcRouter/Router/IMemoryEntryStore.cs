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
}
