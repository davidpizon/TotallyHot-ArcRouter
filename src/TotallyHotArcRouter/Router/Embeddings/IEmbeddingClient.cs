namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Produces a dense embedding vector for a piece of task text, for PLAN.md Phase J's
/// task-embedding-keyed memory. Implementations run entirely in-process - no network hop inline with
/// routing, no dependency on an operator having configured an embeddings-capable provider.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// The identity reported by an implementation that does not name the model it runs. Stamped onto
    /// <see cref="TotallyHot.ArcRouter.Router.MemoryEntry.EmbeddingModel"/> like any other identity, so an
    /// unnamed client's entries still compare equal to each other and unequal to a named client's - the
    /// honest outcome, rather than silently matching everything.
    /// </summary>
    public const string UnknownModelIdentity = "unknown";

    /// <summary>
    /// A stable identity for the model producing these vectors, stamped onto every
    /// <see cref="TotallyHot.ArcRouter.Router.MemoryEntry"/> this client's output is stored in so a later
    /// consumer can tell whether a stored vector is comparable to a freshly computed one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the producer names itself rather than the write site reading configuration.</b> Vector
    /// length alone cannot detect a swap between two different models of the <em>same</em> dimensionality
    /// (1024 is shared by many embedding models), and such a swap is silent: the old and new vectors
    /// occupy incomparable coordinate spaces while every length guard passes. Re-deriving the identity
    /// from <see cref="TotallyHot.ArcRouter.Models.EmbeddingOptions.ModelUrl"/> at each write site would
    /// record what configuration <em>says</em> rather than what actually produced the vector; the two can
    /// disagree (a test fake, a future non-ONNX client), and the column would then lie precisely when it
    /// matters. The component that ran the model is the only one that can answer this honestly.
    /// </para>
    /// <para>
    /// A default implementation returns <see cref="UnknownModelIdentity"/> so the many test fakes
    /// implementing this interface need no change - the same additive default-interface-member convention
    /// <c>IRoutingPolicy.DecideOutcomeAsync</c> established. Production implementations override it.
    /// </para>
    /// </remarks>
    string ModelIdentity => UnknownModelIdentity;

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
