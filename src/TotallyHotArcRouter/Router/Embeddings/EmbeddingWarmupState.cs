namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Tracks whether <see cref="IEmbeddingClient"/> has completed its one-time artifact download and model
/// load (docs/router/live-feedback-learning-plan.md Phase 2b). <see cref="Hosting.StartupHealthCheckHostedService"/>
/// triggers the warm-up off the request path and sets <see cref="IsWarm"/> on success;
/// <see cref="Proxy.RequestInterceptor"/> checks it before ever calling <see cref="IEmbeddingClient.EmbedAsync"/>,
/// so a request arriving before warm-up completes (or one that never completes, e.g. no network access to
/// fetch the ~1.3 GB model) skips embedding entirely instead of triggering the cold download inline.
/// </summary>
public sealed class EmbeddingWarmupState
{
    private volatile bool _isWarm;

    /// <summary>Gets a value indicating whether the embedding client has completed warm-up.</summary>
    public bool IsWarm => _isWarm;

    /// <summary>Marks warm-up as complete. Idempotent.</summary>
    public void MarkWarm()
    {
        _isWarm = true;
    }
}