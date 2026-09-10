using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Counts the tokens a given model would charge for a given piece of prompt text, without asking that
/// model to serve it. This is what makes a per-request routing counterfactual possible: the baseline model
/// never ran, so its token count can only ever be computed, never observed
/// (<c>docs/adr/0009-per-request-counterfactual-token-estimation.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never throws.</b> Every implementation reports failure through the <see langword="bool"/> return
/// rather than an exception, matching <see cref="IUsageLedger"/>'s best-effort contract - a counterfactual
/// is an analytics nicety, and no failure to compute one may propagate into the caller's work.
/// </para>
/// <para>
/// <b>Never guesses silently.</b> A counter that cannot serve a model returns <see langword="false"/>
/// rather than inventing a plausible number, preserving the rule
/// <see cref="Transcripts.ITranscriptStore"/> already states for the estimator it feeds: a caller says
/// "no estimate" instead of pricing an invented token count. Where an approximation *is* returned, the
/// <see cref="TokenCountSource"/> out-parameter says so.
/// </para>
/// <para>
/// <b>Off the hot path.</b> Implementations are consumed only by background analytics
/// (<c>TaxonomyComparisonService</c>) and the calibration sampler. Nothing here is called from
/// <c>ProxyMiddleware</c>, so the cost of tokenizing a large prompt is never paid by a served request.
/// </para>
/// </remarks>
public interface ITokenCounter
{
    /// <summary>
    /// Attempts to count the tokens <paramref name="key"/>'s model would bill for
    /// <paramref name="text"/>.
    /// </summary>
    /// <param name="text">
    /// The prompt text to count. <see langword="null"/>, empty, or whitespace-only input yields
    /// <see langword="false"/> and <see cref="TokenCountSource.Unavailable"/> rather than a count of
    /// <c>0</c> - an absent prompt is not a free prompt, and returning zero would let a caller price a
    /// request that was never characterized as costing nothing.
    /// </param>
    /// <param name="key">The model and provider whose tokenization is being asked about.</param>
    /// <param name="tokens">
    /// The token count when this method returns <see langword="true"/>; <c>0</c> otherwise, which callers
    /// must not read as a measurement.
    /// </param>
    /// <param name="source">
    /// How the count was obtained, including when this method returns <see langword="false"/> (in which
    /// case it is <see cref="TokenCountSource.Unavailable"/>). Always set.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a count - exact or explicitly approximate - was produced;
    /// <see langword="false"/> when none could be.
    /// </returns>
    bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source);
}
