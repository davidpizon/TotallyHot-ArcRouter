using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Adapts sandbox-derived scores into <see cref="EmbeddingMemory"/> (docs/router/live-feedback-learning-plan.md
/// Phase 2c) - the write side of the loop <see cref="Proxy.RequestInterceptor"/>'s Phase 2b embedding
/// computation and <see cref="PendingTaskEmbeddingCache"/> exist to feed. A scored result whose
/// correlation id has no pending embedding (never computed, already claimed, or expired) is a lost
/// learning opportunity, not an error - logged and dropped, exactly like every other best-effort
/// observation path in this codebase.
/// </summary>
public sealed class EmbeddingMemoryScoreObserver : IRouterScoreObserver
{
    private readonly EmbeddingMemory _memory;
    private readonly PendingTaskEmbeddingCache _pendingCache;
    private readonly ILogger<EmbeddingMemoryScoreObserver> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingMemoryScoreObserver"/> class.</summary>
    /// <param name="memory">The embedding-keyed memory to write into.</param>
    /// <param name="pendingCache">The cache bridging a request's embedding to its later-arriving score.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddingMemoryScoreObserver(EmbeddingMemory memory, PendingTaskEmbeddingCache pendingCache, ILogger<EmbeddingMemoryScoreObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(pendingCache);
        ArgumentNullException.ThrowIfNull(logger);

        _memory = memory;
        _pendingCache = pendingCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.Model))
        {
            _logger.LogDebug("Sandbox result has no model attribution; skipping embedding-memory observation.");
            return;
        }

        if (string.IsNullOrEmpty(result.RequestCorrelationId) ||
            !_pendingCache.TryTake(result.RequestCorrelationId, out var embedding) ||
            embedding is null)
        {
            _logger.LogDebug(
                "No pending task embedding for correlation {CorrelationId}; skipping embedding-memory observation.",
                result.RequestCorrelationId);
            return;
        }

        var score = Math.Clamp(result.UnifiedScore, 0.0, 1.0);

        // Cost (κ) is not yet threaded from spend tracking into SandboxResult - no Phase 1-3 consumer needs
        // it, since Phase 3's LogRegVoter scores on embedding alone and cost-aware training is Phase 4/N's
        // concern. Recorded as 0.0 rather than fabricating a value; a future phase that needs κ here must
        // wire a real cost source before relying on this field.
        await _memory.AddEntryAsync(embedding, result.Model, score, cost: 0.0, verifierTrace: null, cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Recorded embedding-memory entry for model {Model} (correlation {CorrelationId}) with score {Score:F3}.",
                result.Model,
                result.RequestCorrelationId,
                score);
        }
    }
}
