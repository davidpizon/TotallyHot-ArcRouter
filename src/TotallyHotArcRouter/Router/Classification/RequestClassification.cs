namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// The Context leg's classification of a single request, per PLAN.md Phase H and the paper's context
/// tuple <c>c_i = (p_i, d_i, H_&lt;i)</c> (research doc §"Notation"). Computed once, ahead of routing,
/// from the request body alone - no network call, no dependency on a response that doesn't exist yet.
/// </summary>
/// <param name="Dimension">
/// One of the research-doc §4.4 dimension vocabulary (code_generation, math, reasoning, multi_language, etc.),
/// or code_generation as the default when nothing more specific is signaled.
/// </param>
/// <param name="Difficulty">One of easy, medium, or hard.</param>
/// <param name="Language">
/// The lowercase name of the programming language detected in the request (e.g. <c>"python"</c>), or
/// <c>"unknown"</c> when no fenced code block with a recognized language hint is present.
/// </param>
/// <param name="IsUtility">
/// <see langword="true"/> when the request looks like lightweight background/auxiliary traffic (a short
/// prompt, a small <c>max_tokens</c> cap) rather than a normal chat turn - the signal
/// <c>docs/router/utility-model-routing.md</c>'s B2 payload heuristics describe.
/// </param>
public sealed record RequestClassification(string Dimension, string Difficulty, string Language, bool IsUtility);
