using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.TestSupport;

/// <summary>
/// An <see cref="IEmbeddingClient"/> that reports a caller-chosen
/// <see cref="IEmbeddingClient.ModelIdentity"/> and refuses to embed anything.
/// </summary>
/// <remarks>
/// Exists for the many collaborators that depend on <see cref="IEmbeddingClient"/> purely to read
/// <see cref="IEmbeddingClient.ModelIdentity"/> - <c>EmbeddingMemory</c>, <c>LogRegVoter</c>,
/// <c>ClusterBestVoter</c> - and never call <see cref="EmbedAsync"/>. Those tests would otherwise each
/// need their own no-op fake purely to satisfy a constructor. <see cref="EmbedAsync"/> throws rather than
/// returning an empty vector so a test that unexpectedly starts embedding fails loudly instead of quietly
/// scoring against zeros.
/// </remarks>
/// <param name="modelIdentity">The identity to report; defaults to <see cref="IEmbeddingClient.UnknownModelIdentity"/>.</param>
internal sealed class StubEmbeddingClient(string? modelIdentity = null) : IEmbeddingClient
{
    /// <inheritdoc />
    public string ModelIdentity { get; } = modelIdentity ?? IEmbeddingClient.UnknownModelIdentity;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always - this stub exists only to report an identity.</exception>
    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{nameof(StubEmbeddingClient)} does not embed; it only reports an identity.");
}
