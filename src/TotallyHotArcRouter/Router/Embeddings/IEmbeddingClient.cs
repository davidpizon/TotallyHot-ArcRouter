namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Produces a dense embedding vector for a piece of task text, for PLAN.md Phase J's
/// task-embedding-keyed memory. Implementations run entirely in-process - no network hop inline with
/// routing, no dependency on an operator having configured an embeddings-capable provider.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Computes the embedding vector for the given text.
    /// </summary>
    /// <param name="text">The task text to embed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The embedding vector together with the token count the model consumed producing it - see
    /// <see cref="EmbeddingResult"/> for why the token count travels with the vector rather than being
    /// discarded.
    /// </returns>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
