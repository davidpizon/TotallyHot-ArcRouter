using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One grader in Phase Q3's LLM grader portfolio (CodeJudge/ICE-Score/RACE): scores a response against its
/// own construct - correctness, usefulness, or readability/maintainability - using the same free provider
/// backbone <see cref="JudgeModelSelector"/> resolves for the G-Eval judge. Mirrors <see cref="IJudgeClient"/>'s
/// shape; a separate interface because a portfolio grader identifies itself by <see cref="GraderKey"/> (it
/// contributes to <see cref="Quality.QualityResult.GraderScores"/>, a keyed map, not a single named field).
/// </summary>
public interface IPortfolioGraderClient
{
    /// <summary>The grader key this client contributes under, e.g. <see cref="GraderKeys.CodeJudge"/>.</summary>
    string GraderKey { get; }

    /// <summary>Scores a single response against this grader's construct.</summary>
    /// <param name="request">The dimension, response text, and originating prompt to score.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The grader's score in <c>[0,1]</c>, or <see langword="null"/> when no free model is currently
    /// eligible to serve as the backbone. Null is an abstention, not a failure - the caller records nothing
    /// rather than a fabricated score.
    /// </returns>
    Task<double?> ScoreAsync(PortfolioGraderScoreRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One scoring request handed to <see cref="IPortfolioGraderClient.ScoreAsync"/>.</summary>
/// <param name="Dimension">The task dimension the response was routed under.</param>
/// <param name="ResponseText">The raw response text to grade.</param>
/// <param name="Prompt">
/// The task the response was written for, or empty when it could not be recovered
/// (docs/research/code-quality-metrics-assessment.md §1: every grader here needs the requirement to score
/// against, not just the answer in isolation).
/// </param>
public sealed record PortfolioGraderScoreRequest(string Dimension, string ResponseText, string Prompt);
