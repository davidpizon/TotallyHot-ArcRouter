namespace TotallyHot.ArcRouter.PriceCatalog.Sources;

/// <summary>
/// One price aggregator's client. A source fetches its published catalog and normalizes it onto the
/// unified <see cref="NormalizedPrice"/> shape - converting to per-million units at this boundary (D2)
/// so the unit conversion is crossed exactly once, in a unit-tested place.
/// </summary>
public interface IPriceSourceClient
{
    /// <summary>Gets the source's registry name (e.g. <c>"litellm"</c>), used for lineage and logging.</summary>
    string Name { get; }

    /// <summary>
    /// Fetches and normalizes this source's current catalog. Unmappable entries are skipped and logged,
    /// never guessed.
    /// </summary>
    Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A single (model, provider) price row after normalization, in USD per 1,000,000 tokens (D2). Every
/// rate is nullable because absent is not zero: a null <see cref="BatchInputPrice"/> means the provider
/// does not offer batch pricing for this model, not that it is free (D7). <see cref="Provider"/> is part
/// of the record because a source publishes a (model, provider) matrix - a normalizer that dropped it
/// could not produce a well-keyed row.
/// </summary>
/// <param name="ModelIdentifier">The source's own model key (resolved to an internal id via <c>model_aliases</c> at ingest, D3).</param>
/// <param name="Provider">The provider that serves this model, e.g. <c>"openai"</c> (D7).</param>
/// <param name="StandardInputPrice">USD per 1M standard input tokens, or <see langword="null"/> if unpublished.</param>
/// <param name="StandardOutputPrice">USD per 1M standard output tokens, or <see langword="null"/> if unpublished.</param>
/// <param name="CachedInputPrice">USD per 1M cached (repeated-context) input tokens, or <see langword="null"/> where caching is not offered.</param>
/// <param name="BatchInputPrice">USD per 1M input tokens at batch/off-peak rates, or <see langword="null"/> where batch is not offered.</param>
/// <param name="BatchOutputPrice">USD per 1M output tokens at batch/off-peak rates, or <see langword="null"/> where batch is not offered.</param>
public sealed record NormalizedPrice(
    string ModelIdentifier,
    string Provider,
    decimal? StandardInputPrice,
    decimal? StandardOutputPrice,
    decimal? CachedInputPrice,
    decimal? BatchInputPrice,
    decimal? BatchOutputPrice);

