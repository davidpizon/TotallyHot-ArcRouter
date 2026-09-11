namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// How many tokens one model spends per token another spends on the same text, together with whether that
/// figure was actually measured or merely assumed.
/// </summary>
/// <param name="Value">The multiplier: <c>baseline tokens / routed tokens</c>. Always strictly positive.</param>
/// <param name="IsMeasured">
/// <see langword="true"/> only when both sides were counted with a tokenizer that genuinely represents
/// their model. <see langword="false"/> means <see cref="Value"/> is a fallback, not an observation.
/// </param>
/// <remarks>
/// <para>
/// This type exists because <c>1.0</c> is dangerously ambiguous on its own. It is the right answer for two
/// models that truly share a tokenizer - and it is also what you get for two models that merely fell back
/// to the same stand-in encoding, which says nothing about how their real tokenizers differ. Arc Router
/// hits the second case constantly: Claude, Gemini, and Mistral all proxy through <c>cl100k_base</c>, so
/// every ratio among them is <c>1.0</c> by construction. Anthropic's own Opus 4.7+ tokenizer runs roughly
/// 1x-1.35x its predecessor, so even a Claude-to-Claude ratio can be materially wrong while looking
/// perfectly ordinary.
/// </para>
/// <para>
/// Carrying the provenance alongside the number is what lets the counterfactual say "the two models were
/// compared" rather than silently implying it - the same refusal to be confidently wrong that
/// <see cref="CostConfidence"/> encodes for price.
/// </para>
/// </remarks>
public readonly record struct TokenizerRatio(double Value, bool IsMeasured)
{
    /// <summary>
    /// The neutral fallback: treat both models as tokenizing identically, and say plainly that this was
    /// not measured.
    /// </summary>
    /// <remarks>
    /// Exactly right when the two models really do share a tokenizer, and a bounded error otherwise -
    /// but the caller is told which it is dealing with rather than left to assume.
    /// </remarks>
    public static TokenizerRatio Assumed => new(Value: 1d, IsMeasured: false);

    /// <summary>
    /// Builds a ratio from two counts of the same text, marking it measured only when both counts came
    /// from a tokenizer that actually represents its model.
    /// </summary>
    /// <param name="baselineTokens">The baseline model's count.</param>
    /// <param name="baselineSource">How the baseline's count was obtained.</param>
    /// <param name="routedTokens">The routed model's count of the same text.</param>
    /// <param name="routedSource">How the routed model's count was obtained.</param>
    /// <returns>The measured ratio, or <see cref="Assumed"/> when it cannot honestly be called one.</returns>
    /// <remarks>
    /// The bar is <see cref="TokenCountSource.LocalCalibrated"/> on <em>both</em> sides: a count that is
    /// native to its model, or a proxy corrected toward its model, each represents that model. An
    /// uncorrected <see cref="TokenCountSource.LocalProxy"/> count does not, and a
    /// <see cref="TokenCountSource.Heuristic"/> one certainly does not - a ratio built from either would
    /// be a statement about the stand-in, not about the models being compared.
    /// </remarks>
    public static TokenizerRatio Measure(
        int baselineTokens,
        TokenCountSource baselineSource,
        int routedTokens,
        TokenCountSource routedSource)
    {
        if (routedTokens <= 0 || baselineTokens <= 0) return Assumed;
        if (baselineSource < TokenCountSource.LocalCalibrated) return Assumed;
        if (routedSource < TokenCountSource.LocalCalibrated) return Assumed;

        return new TokenizerRatio(Value: (double)baselineTokens / routedTokens, IsMeasured: true);
    }
}
