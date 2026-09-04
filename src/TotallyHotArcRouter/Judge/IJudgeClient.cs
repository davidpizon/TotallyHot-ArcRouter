namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A backbone-agnostic G-Eval judge call: scores one response against one dimension's criteria and
/// returns a probability-weighted (or, when the backbone exposes no logprobs, single-sample) score in
/// <c>[0, 1]</c>. Implemented by <see cref="GEvalJudgeClient"/> against whichever free, OpenAI-compatible
/// provider model <see cref="JudgeModelSelector"/> resolves; the seam exists so tests can substitute a
/// fake without any HTTP call.
/// </summary>
public interface IJudgeClient
{
    /// <summary>Scores a single response against a dimension's G-Eval criteria.</summary>
    /// <param name="request">The dimension and response text to score.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The judge's score and whether it was computed via probability weighting, or <see langword="null"/>
    /// when no free model is currently eligible to serve as the backbone. Null is an abstention, not a
    /// failure: the caller records nothing rather than recording a fabricated score, and does not log it
    /// as an error.
    /// </returns>
    Task<JudgeScoreResult?> ScoreAsync(JudgeScoreRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One scoring request handed to <see cref="IJudgeClient.ScoreAsync"/>.</summary>
/// <param name="Dimension">The task dimension whose G-Eval criteria should be applied (e.g. <c>algorithm</c>).</param>
/// <param name="ResponseText">The agent's response text to score.</param>
public sealed record JudgeScoreRequest(string Dimension, string ResponseText);

/// <summary>The result of one <see cref="IJudgeClient.ScoreAsync"/> call.</summary>
/// <param name="Score">The judge's score, normalized to <c>[0, 1]</c>.</param>
/// <param name="UsedLogprobs">
/// Whether <see cref="Score"/> was computed via G-Eval's probability-weighted recipe (the backbone
/// returned token logprobs) rather than the single-sample numeric-score fallback.
/// </param>
/// <param name="JudgeModel">
/// The client-facing name of the model that actually produced this score, stamped onto the shadow row's
/// <c>judge_model</c> column. Reported by the client rather than read back from configuration because
/// <see cref="JudgeModelSelector"/> may have substituted a fallback for an ineligible configured pick -
/// the row must name what ran, since G2's agreement analysis segments on it.
/// </param>
public sealed record JudgeScoreResult(double Score, bool UsedLogprobs, string JudgeModel);