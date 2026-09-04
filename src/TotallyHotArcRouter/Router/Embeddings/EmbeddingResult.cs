namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// One embedding computation's output: the vector itself plus the token count the model actually
/// consumed producing it. The token count is carried alongside the vector rather than discarded because
/// it is the router's <em>own</em> token consumption, which the research doc's cost accounting charges
/// against the router: §5.1 defines <c>TotTok</c> as "total input + output token consumption
/// (<b>router</b> + model)", so a savings figure computed from model tokens alone reports gross rather
/// than net. The embedding client is the only component that knows this number - it falls out of
/// tokenization and is invisible to every caller downstream - so it is returned here instead of being
/// re-derived by approximation later.
/// </summary>
/// <param name="Vector">
/// The unit-normalized embedding vector, so a plain dot product between two results equals their cosine
/// similarity.
/// </param>
/// <param name="TokenCount">
/// The number of tokens the embedding model processed, after truncation to
/// <see cref="TotallyHot.ArcRouter.Models.EmbeddingOptions.MaxTokens"/>. Input tokens only: an embedding
/// model emits a vector, not tokens, so there is no output half to count.
/// </param>
public readonly record struct EmbeddingResult(float[] Vector, int TokenCount);