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
    private readonly PendingRequestCostCache _pendingCostCache;
    private readonly PendingRequestProvenanceCache _pendingProvenanceCache;
    private readonly ILogger<EmbeddingMemoryScoreObserver> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingMemoryScoreObserver"/> class.</summary>
    /// <param name="memory">The embedding-keyed memory to write into.</param>
    /// <param name="pendingCache">The cache bridging a request's embedding to its later-arriving score.</param>
    /// <param name="pendingCostCache">The cache bridging a request's estimated dollar cost to its later-arriving score.</param>
    /// <param name="pendingProvenanceCache">The cache bridging a request's exploration provenance to its later-arriving score.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddingMemoryScoreObserver(
        EmbeddingMemory memory,
        PendingTaskEmbeddingCache pendingCache,
        PendingRequestCostCache pendingCostCache,
        PendingRequestProvenanceCache pendingProvenanceCache,
        ILogger<EmbeddingMemoryScoreObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(pendingCache);
        ArgumentNullException.ThrowIfNull(pendingCostCache);
        ArgumentNullException.ThrowIfNull(pendingProvenanceCache);
        ArgumentNullException.ThrowIfNull(logger);

        _memory = memory;
        _pendingCache = pendingCache;
        _pendingCostCache = pendingCostCache;
        _pendingProvenanceCache = pendingProvenanceCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        // TryTake runs first, unconditionally consuming the pending-cache slot, so a result with no model
        // attribution still drains its entry instead of leaving it to age out via TTL/capacity eviction.
        if (string.IsNullOrEmpty(result.RequestCorrelationId) ||
            !_pendingCache.TryTake(result.RequestCorrelationId, out var embedding) ||
            embedding is null)
        {
            _logger.LogDebug(
                "No pending task embedding for correlation {CorrelationId}; skipping embedding-memory observation.",
                result.RequestCorrelationId);
            return;
        }

        if (string.IsNullOrEmpty(result.Model))
        {
            _logger.LogDebug("Sandbox result has no model attribution; skipping embedding-memory observation.");
            return;
        }

        var score = Math.Clamp(result.UnifiedScore, 0.0, 1.0);

        // docs/router/self-organizing-classification-plan.md Phase T1c: cost, IsExploratory, and
        // Propensity are all known at request-resolution time (ModelRouteResolutionResult) but only
        // become available here, keyed by the same correlation id, once the verifier score arrives -
        // exactly the timing problem PendingTaskEmbeddingCache already solves for the embedding. Three
        // parallel TryTake calls, not one merged lookup, because ProxyMiddleware sets the three caches
        // independently (they come from different points in that method) and a miss on any one of them
        // must not block recording the other two - a cost never recorded is still worth an embedding
        // memory entry.
        var recoveredCost = 0.0;
        if (!string.IsNullOrEmpty(result.RequestCorrelationId) && _pendingCostCache.TryTake(result.RequestCorrelationId, out var cachedCost))
        {
            recoveredCost = (double)cachedCost;
        }
        else
        {
            _logger.LogDebug(
                "No pending cost for correlation {CorrelationId}; recording embedding-memory entry with cost 0.",
                result.RequestCorrelationId);
        }

        var recoveredIsExploratory = false;
        var recoveredPropensity = 1.0;
        string? recoveredDimension = null;
        if (!string.IsNullOrEmpty(result.RequestCorrelationId) &&
            _pendingProvenanceCache.TryTake(result.RequestCorrelationId, out var cachedIsExploratory, out var cachedPropensity, out var cachedDimension))
        {
            recoveredIsExploratory = cachedIsExploratory;
            recoveredPropensity = cachedPropensity;
            recoveredDimension = cachedDimension;
        }
        else
        {
            _logger.LogDebug(
                "No pending provenance for correlation {CorrelationId}; recording embedding-memory entry as non-exploratory, propensity 1.0.",
                result.RequestCorrelationId);
        }

        await _memory.AddEntryAsync(
            embedding,
            result.Model,
            score,
            cost: recoveredCost,
            verifierTrace: null,
            cancellationToken,
            isExploratory: recoveredIsExploratory,
            propensity: recoveredPropensity,
            dimension: recoveredDimension).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Recorded embedding-memory entry for model {Model} (correlation {CorrelationId}) with score {Score:F3}, cost {Cost:F6}, exploratory {IsExploratory}.",
                result.Model,
                result.RequestCorrelationId,
                score,
                recoveredCost,
                recoveredIsExploratory);
        }
    }
}
