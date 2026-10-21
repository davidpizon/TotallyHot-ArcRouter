using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Truncating wrapper over <see cref="PendingValueCache{T}"/> for a request's
/// <see cref="ConversationWindowResult"/>. Consumed once when a <see cref="GradeReceiptRecord"/> is
/// written; graders read the formatted text through <see cref="PendingPromptCache"/> instead.
/// </summary>
public sealed class PendingConversationWindowCache
{
    private readonly PendingValueCache<ConversationWindowResult> _inner;

    /// <summary>Initializes a new instance of the <see cref="PendingConversationWindowCache"/> class.</summary>
    /// <param name="options">Supplies the capacity and TTL bounds shared with other pending caches.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public PendingConversationWindowCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new PendingValueCache<ConversationWindowResult>(options, timeProvider);
    }

    /// <summary>Records <paramref name="window"/> under <paramref name="correlationId"/>.</summary>
    /// <param name="correlationId">The request correlation id the receipt observer will look up.</param>
    /// <param name="window">The bounded conversation window extracted on the hot path.</param>
    public void Set(string correlationId, ConversationWindowResult window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _inner.Set(correlationId, window);
    }

    /// <summary>
    /// Reads the window recorded under <paramref name="correlationId"/> without removing it.
    /// </summary>
    /// <param name="correlationId">The correlation id to look up.</param>
    /// <param name="window">The cached window when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryPeek(string correlationId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConversationWindowResult? window) =>
        _inner.TryPeek(correlationId, out window);

    /// <summary>
    /// Removes and returns the window recorded under <paramref name="correlationId"/>, if any.
    /// </summary>
    /// <param name="correlationId">The correlation id to look up.</param>
    /// <param name="window">The cached window when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryTake(string correlationId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConversationWindowResult? window) =>
        _inner.TryTake(correlationId, out window);
}
