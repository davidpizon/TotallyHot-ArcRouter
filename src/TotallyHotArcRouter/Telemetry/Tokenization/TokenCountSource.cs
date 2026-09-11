namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// How a token count was arrived at, ordered from least to most trustworthy. Exists for the same reason
/// <see cref="CostConfidence"/> does: the several ways a count can be approximate are materially different
/// answers, and returning a bare <see cref="int"/> would let a character-count guess and a provider-exact
/// measurement reach the Routing ROI chart wearing the same label
/// (<c>docs/adr/0009-per-request-counterfactual-token-estimation.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The declaration order is deliberate and load-bearing: members ascend in trust, so a caller choosing
/// between two candidate counts can compare them with <c>&gt;</c> instead of restating a precedence table.
/// Do not reorder or insert into the middle without auditing every comparison.
/// </para>
/// <para>
/// This deliberately parallels <see cref="CostConfidence"/> rather than reusing it. The two answer
/// different questions - "how well do we know the token count?" versus "how well do we know the price?" -
/// and a request can be exact on one and unknown on the other. Phase 5 of
/// <c>docs/router/counterfactual-token-estimation-plan.md</c> maps this onto that, which is only possible
/// because they are kept separate here.
/// </para>
/// </remarks>
public enum TokenCountSource
{
    /// <summary>
    /// No count could be produced at all - there was no text to count, or no counter would serve the
    /// model. Distinct from a count of <c>0</c>: absent is not zero, the same distinction
    /// <see cref="CostConfidence.NoUsage"/> draws for cost.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Estimated from character length alone, with no tokenizer involved. A floor of last resort for a
    /// model whose family has no available encoding; never to be presented as a measurement.
    /// </summary>
    Heuristic,

    /// <summary>
    /// Counted with a <em>foreign</em> encoding standing in for this model's real tokenizer, with no
    /// calibration factor applied - either because none has been learned yet, or because the one on record
    /// has too few samples to trust. A systematic under-count for Claude (roughly 15-20% on prose, more on
    /// code) and an unquantified one for Gemini and Mistral.
    /// </summary>
    /// <remarks>
    /// The important consequence is comparative, not absolute: two models that both land here share an
    /// encoding by accident of fallback rather than by fact, so a ratio computed between them is
    /// <c>1.0</c> by construction and says nothing about how their real tokenizers differ. That is why
    /// <see cref="TokenizerRatio"/> refuses to call such a ratio measured.
    /// </remarks>
    LocalProxy,

    /// <summary>
    /// Counted with a foreign encoding, then scaled by a calibration factor learned from the provider's own
    /// counting endpoint. The normal steady state for a model the calibration sampler has observed, and the
    /// point at which a ratio against another model becomes meaningful again.
    /// </summary>
    LocalCalibrated,

    /// <summary>
    /// Counted with the model's <em>own</em> tokenizer - the OpenAI families whose tiktoken encoding this
    /// genuinely is, rather than a stand-in. Ranks above <see cref="LocalCalibrated"/> because it needs no
    /// correction: it is the real thing, short only of the provider's own message-framing overhead.
    /// </summary>
    LocalNative,

    /// <summary>
    /// Counted by the provider itself for this exact text and model. Reserved for the calibration sampler's
    /// own readings - the estimator never takes this path, because requiring it would make an offline
    /// router unable to compute a counterfactual at all.
    /// </summary>
    ProviderExact
}
