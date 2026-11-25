namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Rewraps sealed <c>memory_entries.embedding</c> blobs when the session master key rotates
/// (ADR-0029). Plaintext rows written before encryption are left as they are. The request path
/// does not call this: kNN searches the copy <see cref="EmbeddingMemory"/> already holds.
/// </summary>
public interface IQuestionVectorRewrap
{
    /// <summary>
    /// Rewrites sealed question vectors from <paramref name="fromKey"/> to <paramref name="toKey"/>.
    /// A blob that already opens under <paramref name="toKey"/> is left. A raw float32 blob is left.
    /// A sealed blob that opens under neither key is deleted, so it cannot be read as a neighbor.
    /// Calling this again with the same keys changes nothing.
    /// </summary>
    /// <param name="fromKey">The 32-byte master key the blobs were sealed with.</param>
    /// <param name="toKey">The 32-byte master key that will replace it.</param>
    void Rewrap(byte[] fromKey, byte[] toKey);

    /// <summary>
    /// Whether any sealed blob opens under <paramref name="key"/> and not under <paramref name="other"/>.
    /// Startup recovery uses this when a rotation was interrupted and the session index has no row to
    /// decide which key won.
    /// </summary>
    /// <param name="key">The key that may already wrap a question vector.</param>
    /// <param name="other">The key that must not also open that same blob.</param>
    /// <returns><see langword="true"/> when a sealed blob authenticates only under <paramref name="key"/>.</returns>
    bool HasBlobOnlyUnder(byte[] key, byte[] other);
}
