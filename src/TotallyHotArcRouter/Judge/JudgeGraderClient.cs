using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Adapts <see cref="IJudgeClient"/> onto <see cref="IPortfolioGraderClient"/> so the shared drain can
/// look up every LLM grader - including G-Eval - by <see cref="GraderKeys.Judge"/>.
/// </summary>
internal sealed class JudgeGraderClient : IPortfolioGraderClient
{
    private readonly IJudgeClient _judgeClient;

    /// <summary>Initializes a new instance of the <see cref="JudgeGraderClient"/> class.</summary>
    /// <param name="judgeClient">The G-Eval judge client to adapt.</param>
    public JudgeGraderClient(IJudgeClient judgeClient)
    {
        ArgumentNullException.ThrowIfNull(judgeClient);
        _judgeClient = judgeClient;
    }

    /// <inheritdoc/>
    public string GraderKey => GraderKeys.Judge;

    /// <inheritdoc/>
    public async Task<PortfolioGraderScoreResult?> ScoreAsync(PortfolioGraderScoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _judgeClient
            .ScoreAsync(
                request: new JudgeScoreRequest(Dimension: request.Dimension, ResponseText: request.ResponseText,
                    Prompt: request.Prompt),
                cancellationToken: cancellationToken).ConfigureAwait(false);

        return result is null
            ? null
            : new PortfolioGraderScoreResult(Score: result.Score, GraderModel: result.JudgeModel,
                UsedLogprobs: result.UsedLogprobs);
    }
}
