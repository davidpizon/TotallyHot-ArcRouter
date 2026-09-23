using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Truncating wrapper over <see cref="PendingValueCache{T}"/> for a request's raw response text. Two
/// string caches (this and <see cref="PendingPromptCache"/>) cannot share one DI identity, and a per-entry
/// character cap is the only behavior the generic does not already provide. Multi-reader: graders
/// <see cref="TryPeek"/>; the slot is released only by TTL or capacity eviction.
/// </summary>
public sealed class PendingResponseTextCache
{
    private readonly PendingValueCache<string> _inner;
    private readonly int _maxTextChars;

    /// <summary>Initializes a new instance of the <see cref="PendingResponseTextCache"/> class.</summary>
    /// <param name="options">Supplies the capacity, TTL, and per-entry text-size bounds.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public PendingResponseTextCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _inner = new PendingValueCache<string>(options, timeProvider);
        _maxTextChars = options.Value.MaxCachedTextChars;
    }

    /// <summary>Gets the number of entries currently held (test/diagnostic use).</summary>
    internal int Count => _inner.Count;

    /// <summary>
    /// Records <paramref name="text"/> under <paramref name="correlationId"/>, truncating to the
    /// configured per-entry character cap first.
    /// </summary>
    /// <param name="correlationId">The request correlation id later graders will look up.</param>
    /// <param name="text">The response text to retain; truncated if longer than the configured cap.</param>
    public void Set(string correlationId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bounded = text.Length > _maxTextChars ? text[.._maxTextChars] : text;
        _inner.Set(correlationId, bounded);
    }

    /// <summary>
    /// Reads the response text recorded under <paramref name="correlationId"/> without removing it, so
    /// concurrently-dispatched graders for the same request can still read it.
    /// </summary>
    /// <param name="correlationId">The request correlation id to look up.</param>
    /// <param name="text">The cached text when this returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryPeek(string correlationId, out string? text)
    {
        return _inner.TryPeek(correlationId, out text);
    }
}
