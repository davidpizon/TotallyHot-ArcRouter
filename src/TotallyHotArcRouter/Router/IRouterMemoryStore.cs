using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Defines the contract for a component that persists and retrieves router memory.
/// </summary>
/// <remarks>
/// <para>
/// The write side records <em>one observation at a time</em> rather than accepting a whole-memory snapshot
/// the way this interface previously did. That older shape was an artifact of a JSON-file implementation,
/// and it forced every score to rewrite the entire accumulated history; a per-observation write lets a
/// database fold the score in with a single statement. This mirrors
/// <see cref="IMemoryEntryStore"/>/<see cref="SqliteMemoryEntryStore"/>, the same load-all-once,
/// append-per-item split already used for embedding-keyed memory.
/// </para>
/// <para>
/// Implementations must make <see cref="RecordScoreAsync"/> safe to call concurrently for the same
/// (dimension, model) pair without losing an observation - see
/// <see cref="SqliteRouterMemoryStore.RecordScoreAsync"/> for why a read-modify-write would not be.
/// </para>
/// </remarks>
public interface IRouterMemoryStore
{
    /// <summary>
    /// Loads every persisted score aggregate, keyed by dimension then model.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stored aggregates, empty when nothing has been persisted yet.</returns>
    Task<ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>> LoadAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds a single observed score into the persisted aggregate for one (dimension, model) pair,
    /// creating the row when it is the pair's first observation.
    /// </summary>
    /// <param name="dimension">The dimension the score was observed under.</param>
    /// <param name="model">The model that was scored.</param>
    /// <param name="score">The observed score.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RecordScoreAsync(
        string dimension,
        string model,
        double score,
        CancellationToken cancellationToken = default);
}
