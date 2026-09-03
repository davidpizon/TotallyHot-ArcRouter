namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Identifies one priced thing: a model <em>as served by a particular provider</em>. Both halves are
/// required - the same model costs different amounts on different providers, and whether it offers
/// cached or batch rates at all is a provider fact (see <c>docs/router/model-price-catalog.md</c> D7).
/// A record rather than two loose string parameters so a caller cannot supply half of it or transpose
/// the two.
/// </summary>
/// <param name="ModelName">
/// The internal <c>models.model_identifier</c> the catalog stores and joins on. D3's design targets the
/// client-facing <c>ModelRouting:ModelList[].ModelName</c>, resolving each aggregator's own naming onto
/// it at ingest via <c>model_aliases</c>. <b>Until D3 alias resolution is implemented</b>, that mapping
/// is 1:1 identity: the repository stores each source's own model key verbatim (e.g. LiteLLM's
/// <c>"gpt-4o"</c>) as the identifier, and <c>GetFreshPrice</c> matches on it - so a caller must pass the
/// source's key today, not necessarily the routing name. Both coincide only where the two happen to be
/// equal.
/// </param>
/// <param name="Provider">The <c>ModelRouting:Providers</c> key, e.g. <c>"openai"</c>.</param>
public readonly record struct ModelKey(string ModelName, string Provider);