namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The sealed <c>response_vectors</c> table. Callers pass plaintext floats; the store seals them before
/// the write and never persists the plaintext or the response text.
/// </summary>
public interface IResponseVectorStore
{
    /// <summary>
    /// Seals and upserts one response vector for <paramref name="correlationId"/>.
    /// </summary>
    /// <param name="correlationId">The request correlation id. Primary key.</param>
    /// <param name="embeddingModel">The BGE model id that produced <paramref name="vector"/>.</param>
    /// <param name="vector">The plaintext vector. It is sealed before it is stored.</param>
    /// <param name="createdAtUtc">When the grade wrote the row. Retention orders by this.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpsertAsync(string correlationId, string embeddingModel, float[] vector, DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the rows whose correlation ids are in <paramref name="correlationIds"/>.</summary>
    /// <param name="correlationIds">The ids to remove. Empty is a no-op.</param>
    void DeleteByCorrelationIds(IReadOnlyList<string> correlationIds);
}
