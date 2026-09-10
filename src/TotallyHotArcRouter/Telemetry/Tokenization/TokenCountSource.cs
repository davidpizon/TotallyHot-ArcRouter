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
    /// Counted by a real local tokenizer, but with no calibration factor applied - either because none has
    /// been learned for this model yet, or because the one on record has too few samples to trust. Correct
    /// for the OpenAI families whose encoding this actually is, and a systematic under-count for others
    /// (notably Claude, by roughly 15-20% on prose and more on code).
    /// </summary>
    LocalUncalibrated,

    /// <summary>
    /// Counted by a local tokenizer and scaled by a calibration factor learned from the provider's own
    /// counting endpoint. The normal steady state for a model the calibration sampler has observed.
    /// </summary>
    LocalCalibrated,

    /// <summary>
    /// Counted by the provider itself for this exact text and model. Reserved for the calibration sampler's
    /// own readings - the estimator never takes this path, because requiring it would make an offline
    /// router unable to compute a counterfactual at all.
    /// </summary>
    ProviderExact
}
