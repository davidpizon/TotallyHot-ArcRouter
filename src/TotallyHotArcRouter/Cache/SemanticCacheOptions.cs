using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Cache;

/// <summary>
/// Opt-in settings for the local semantic response cache, bound from the <c>SemanticCache</c> section.
/// </summary>
/// <remarks>
/// <para>
/// Off unless an operator sets <see cref="Enabled"/>. The cache reuses a saved completion when a later
/// request means the same thing as an earlier one. It is a different mechanism from provider prompt
/// caching: nothing here changes <c>cache_read_tokens</c> or the Cost Analytics "Cache Hit" chart
/// (<c>CostChartBuilder</c>). See <c>docs/router/semantic-cache.md</c>.
/// </para>
/// <para>
/// <see cref="SimilarityThreshold"/> defaults to 0.92, deliberately far above
/// <c>Routing:EmbeddingSimilarityThreshold</c> (0.5). Routing can use a loose neighbor as a vote for
/// which model to pick. This cache replays the answer itself, so a false hit is a wrong completion.
/// 0.92 keeps reuse for near-paraphrases on the unit-normalized vectors
/// <see cref="TotallyHot.ArcRouter.Router.Embeddings.IEmbeddingClient"/> already produces, and leaves
/// merely related questions to the provider. It is a starting point, not a measurement from this
/// repository's corpus; lower it only after checking that reused answers are still the ones you wanted.
/// </para>
/// </remarks>
public sealed class SemanticCacheOptions
{
    /// <summary>Gets the configuration section name.</summary>
    public const string SectionName = "SemanticCache";

    /// <summary>
    /// Gets whether the cache looks up and stores answers. <see langword="false"/> by default, which
    /// performs no lookup and no store and leaves request handling unchanged.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the minimum cosine similarity, in (0, 1], required to reuse a saved answer. Compared with
    /// <see cref="TotallyHot.ArcRouter.Router.EmbeddingMemory.CosineSimilarity"/>, the same function
    /// routing kNN uses.
    /// Defaults to 0.92; see the type remarks for why that is stricter than the routing neighbor bar.
    /// </summary>
    public double SimilarityThreshold { get; init; } = 0.92;

    /// <summary>
    /// Gets how long a stored answer may be served. Measured from the moment it was stored, using the
    /// value current at lookup time, so shortening this expires existing entries on the next request
    /// and lengthening it can serve an entry that is still inside the new window. Defaults to one hour:
    /// long enough to absorb a repeated question in a working session, short enough that an answer about
    /// a changing codebase does not live all day.
    /// </summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the maximum number of answers kept. When a store would exceed this, the oldest entries are
    /// dropped. Defaults to 256, a bound for a single-operator process rather than a shared cluster.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxEntries { get; init; } = 256;

    /// <summary>
    /// Gets the largest response body, in bytes, that may be stored. Larger bodies are not cached.
    /// Must stay strictly below the proxy's response capture cap (4 MiB) so a truncated capture is never
    /// replayed as a complete answer. Defaults to 1 MiB.
    /// </summary>
    [Range(1, 4_194_303)]
    public int MaxStoredResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Gets a generation counter. Raising it drops every stored answer on the next lookup or store, which
    /// is the config-file way to clear the cache and to invalidate entries after a change that would make
    /// saved answers wrong. Defaults to 0.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int CacheEpoch { get; init; }

    /// <summary>
    /// Checks bounds that data annotations do not express, including the open interval for similarity
    /// and the capture-cap rule for stored bodies.
    /// </summary>
    /// <exception cref="OptionsValidationException">A value is outside the documented range.</exception>
    public void EnsureValid()
    {
        if (SimilarityThreshold is <= 0 or > 1)
            throw new OptionsValidationException(
                optionsName: nameof(SemanticCacheOptions),
                optionsType: typeof(SemanticCacheOptions),
                failureMessages:
                [$"{nameof(SimilarityThreshold)} must be in the interval (0, 1], but was {SimilarityThreshold}."]);

        if (TimeToLive <= TimeSpan.Zero)
            throw new OptionsValidationException(
                optionsName: nameof(SemanticCacheOptions),
                optionsType: typeof(SemanticCacheOptions),
                failureMessages: [$"{nameof(TimeToLive)} must be positive, but was {TimeToLive}."]);

        if (MaxEntries < 1)
            throw new OptionsValidationException(
                optionsName: nameof(SemanticCacheOptions),
                optionsType: typeof(SemanticCacheOptions),
                failureMessages: [$"{nameof(MaxEntries)} must be at least 1, but was {MaxEntries}."]);

        if (MaxStoredResponseBytes < 1 ||
            MaxStoredResponseBytes >= UpstreamResponseWriter.MaxCapturedResponseBytes)
            throw new OptionsValidationException(
                optionsName: nameof(SemanticCacheOptions),
                optionsType: typeof(SemanticCacheOptions),
                failureMessages:
                [
                    $"{nameof(MaxStoredResponseBytes)} must be at least 1 and strictly less than {UpstreamResponseWriter.MaxCapturedResponseBytes} (the response capture cap), but was {MaxStoredResponseBytes}."
                ]);

        if (CacheEpoch < 0)
            throw new OptionsValidationException(
                optionsName: nameof(SemanticCacheOptions),
                optionsType: typeof(SemanticCacheOptions),
                failureMessages: [$"{nameof(CacheEpoch)} must be zero or positive, but was {CacheEpoch}."]);
    }
}
