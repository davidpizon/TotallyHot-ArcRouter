using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Truncating wrapper over <see cref="PendingValueCache{T}"/> for a request's prompt text. Distinct from
/// <see cref="PendingResponseTextCache"/> only so the two string caches remain separately injectable;
/// both share <see cref="JudgeOptions"/> bounds and the same multi-reader <see cref="TryPeek"/> contract.
/// </summary>
public sealed class PendingPromptCache
{
    private readonly PendingValueCache<string> _inner;
    private readonly int _maxTextChars;

    /// <summary>Initializes a new instance of the <see cref="PendingPromptCache"/> class.</summary>
    /// <param name="options">Supplies the capacity, TTL, and per-entry text-size bounds.</param>
    /// <param name="timeProvider">Clock used for TTL expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public PendingPromptCache(IOptions<JudgeOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _inner = new PendingValueCache<string>(options, timeProvider);
        _maxTextChars = options.Value.MaxCachedTextChars;
    }

    /// <summary>Gets the number of entries currently held (test/diagnostic use).</summary>
    internal int Count => _inner.Count;

    /// <summary>
    /// Records <paramref name="prompt"/> under <paramref name="correlationId"/>, truncating to the
    /// configured per-entry character cap first.
    /// </summary>
    /// <param name="correlationId">The request correlation id later graders will look up.</param>
    /// <param name="prompt">The prompt text to retain; truncated if longer than the configured cap.</param>
    public void Set(string correlationId, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var bounded = prompt.Length > _maxTextChars ? prompt[.._maxTextChars] : prompt;
        _inner.Set(correlationId, bounded);
    }

    /// <summary>
    /// Reads the prompt text recorded under <paramref name="correlationId"/> without removing it.
    /// </summary>
    /// <param name="correlationId">The request correlation id to look up.</param>
    /// <param name="prompt">The cached prompt when this returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an unexpired entry was found.</returns>
    public bool TryPeek(string correlationId, out string? prompt)
    {
        return _inner.TryPeek(correlationId, out prompt);
    }
}
