namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Writes back what one response revealed about how its model expresses a tool call - tier 4 of
/// <c>docs/router/tool-call-normalization.md</c> §3.2. One instance per response; not thread-safe.
///
/// <para>
/// <b>Only an evidence-bearing response is recorded.</b> A tools-carrying request whose response is
/// ordinary prose is the overwhelmingly common case - the model simply had no reason to call a tool - and
/// it says nothing whatsoever about which dialect that model speaks. The design sketch in §3.2 proposed
/// condemning a model to emulation after several unmatched responses; that is not implemented here,
/// because at this layer "chose not to call a tool" and "cannot call tools" produce byte-identical
/// evidence, so such a counter would downgrade well-behaved models for answering questions. Selecting
/// emulation needs a signal this phase does not have, and belongs to Phase 5.
/// </para>
/// </summary>
internal sealed class ToolCallObservationRecorder
{
    private readonly ToolCallNormalizationPlan _plan;
    private readonly IToolCallCapabilityStore? _store;

    // One classification per response. A streaming reply can carry several tool calls across many chunks,
    // and each would otherwise re-record the identical row - a database write and a full cache reload per
    // chunk, on the request path.
    private bool _recorded;

    /// <summary>Initializes a new instance of the <see cref="ToolCallObservationRecorder"/> class.</summary>
    /// <param name="plan">The plan this response was scanned under; supplies the store key and whether to record at all.</param>
    /// <param name="store">The capability store to write to, or <see langword="null"/> to observe nothing.</param>
    public ToolCallObservationRecorder(ToolCallNormalizationPlan plan, IToolCallCapabilityStore? store)
    {
        _plan = plan;
        _store = store;
    }

    /// <summary>
    /// Records that the model emitted real OpenAI <c>tool_calls</c> and needs no rewriting - so every
    /// later request for it reverts to unarmed byte-for-byte passthrough (performance rule 2).
    /// </summary>
    public void RecordNative() =>
        Record(ToolCallDialectRegistry.OpenAiNative.Name, "Response carried native tool_calls.");

    /// <summary>
    /// Records that <paramref name="dialect"/>'s framing matched and yielded a well-shaped call, so later
    /// requests arm only it.
    /// </summary>
    /// <param name="dialect">The dialect that matched.</param>
    public void RecordMatch(ToolCallDialect dialect)
    {
        if (_plan.IsEmulating)
        {
            // The model framed a call in this dialect because TotallyHot.ArcRouter's own injected system prompt
            // instructed it to (Phase 5). Recording that as an observation would be circular, and worse
            // than useless: DetectionConfidence.Observed outranks the `emulated` row that produced the
            // instructions, so the next request would arrive un-emulated, the instructions would be gone,
            // and the model would emit nothing - a classification that erases the reason it was made.
            return;
        }

        Record(dialect.Name, $"Response framed a tool call in the {dialect.Name} dialect.");
    }

    /// <summary>
    /// Persists one classification at <see cref="DetectionConfidence.Observed"/>, which outranks the
    /// template and model-id tiers but never an operator's pin - the store's confidence gate enforces that.
    /// </summary>
    private void Record(string dialectName, string evidence)
    {
        if (!_plan.IsObserving || _recorded || _store is null)
        {
            return;
        }

        _recorded = true;

        // Evidence is a *description*, never the matched text: a real tool call carries the user's own
        // arguments, and this row lands in a database with no retention policy. See ModelToolCapability.Evidence.
        //
        // ObservationCount is passed as a flat 1 rather than read-then-incremented. It seeds the row on an
        // insert; on an existing row the repository adds 1 in SQL. Reading the current count here to
        // compute a successor would let two concurrent first-responses for the same model both read 0 and
        // both write 1 - and concurrent first-responses are exactly the case this path exists for. It also
        // keeps a database read off the request path.
        _store.TryRecordModelCapability(new ModelToolCapability(
            _plan.ProviderKey,
            _plan.ModelName,
            dialectName,
            DetectionConfidence.Observed,
            evidence,
            ObservationCount: 1));
    }
}

