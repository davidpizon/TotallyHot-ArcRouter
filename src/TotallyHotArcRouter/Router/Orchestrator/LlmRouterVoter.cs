namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>llm_router</c> voter seam (PLAN.md Phase L): research-doc §3.3/A.1 specifies this voter as the
/// paper's own fine-tuned Qwen3.5-0.8B, hosted locally via ONNX Runtime, reached over the +Perf-stats
/// prompt of Appendix B.3. This implementation is deliberately a stub that always abstains - Phase L's
/// scope, agreed with the user ahead of implementation, is the three real voters
/// (<see cref="DimBestVoter"/>, <see cref="MemoryKnnVoter"/>, <see cref="LogRegVoter"/>) plus this
/// documented, always-abstaining seam, exercising exactly the degrade path PLAN.md Phase L requires
/// ("must degrade to a three-voter vote - never to a hard failure - when the model artifact isn't present
/// yet").
/// </summary>
/// <remarks>
/// <para>
/// <b>What is deferred here, and why:</b> sourcing/exporting the fine-tuned Qwen3.5-0.8B weights to ONNX
/// (or a documented fine-tune-compatible substitute), the tokenizer, autoregressive generation (KV-cache,
/// sampling, one forward pass per output token - a materially larger scope than Phase J's single-pass
/// embedding inference), the four-step response-parsing fallback chain (JSON → fenced-block regex →
/// model-name match → default), the disagreement-gated invocation cost control, and the generation-timeout
/// budget are all out of scope for this phase per the deliberate scope cut agreed before implementation
/// began (see PLAN.md Phase L's deferral note). <see cref="OrchestratorRoutingPolicy"/> is built to treat
/// this voter's permanent abstention as the normal three-voter case, not as a special one, so filling in
/// this seam later requires no change to the ensemble's degrade logic - only this class's body.
/// </para>
/// <para>
/// This class exists (rather than simply omitting the voter) so <see cref="OrchestratorRoutingPolicy"/>'s
/// voter list, weight configuration, and telemetry are already shaped for a fourth voter, and so a future
/// session implementing the real model only has to replace <see cref="VoteAsync"/>'s body.
/// </para>
/// </remarks>
public sealed class LlmRouterVoter : IRoutingVoter
{
    /// <inheritdoc />
    public string Name => VoterNames.LlmRouter;

    /// <inheritdoc />
    /// <remarks>Always abstains - see the class remarks for why.</remarks>
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(VoterVote.Abstain(Name));
    }
}
