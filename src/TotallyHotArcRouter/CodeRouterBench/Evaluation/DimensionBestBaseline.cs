namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The <c>DimensionBest</c> baseline (research-doc Table 4): argmax over the frozen probing-split
/// (dimension, model) average from <see cref="DimensionModelScoreMatrix"/> — deliberately the frozen
/// prior, not <see cref="Router.Orchestrator.DimBestVoter"/>'s live-memory-preferring version, per Table
/// 4's "frozen probing-set prior" for the static-classifier family.
/// </summary>
public sealed class DimensionBestBaseline : IRegretBaselineRouter
{
    private readonly DimensionModelScoreMatrix _matrix;

    /// <summary>Initializes a new instance of the <see cref="DimensionBestBaseline"/> class.</summary>
    /// <param name="matrix">
    /// The frozen probing-split score matrix, e.g. from
    /// <see cref="DimensionModelScoreMatrix.FromDatabase"/>.
    /// </param>
    public DimensionBestBaseline(DimensionModelScoreMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        _matrix = matrix;
    }

    /// <inheritdoc/>
    public string Name => "dim_best";

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to <see cref="DimensionModelScoreMatrix.SelectBest"/> - the same argmax-and-tie-break
    /// rule the live ROI yardstick (<see cref="Router.UntrainedBaselineSelector"/>) now shares, so this
    /// baseline and the production "untrained router" comparison can never quietly diverge. Returns
    /// <see langword="null"/> when the frozen matrix has no average for any of this task's candidates.
    /// </remarks>
    public string? Route(RegretReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _matrix.SelectBest(dimension: context.Dimension, candidateModelIds: context.CandidateModelIds);
    }
}