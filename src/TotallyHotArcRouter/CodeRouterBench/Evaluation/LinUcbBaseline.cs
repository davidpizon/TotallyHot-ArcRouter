namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The LinUCB baseline (research-doc Table 4), categorical-context variant: one-hot over dimension, the
/// canonical parameters <c>α = λ = 1</c>, online per-arm posterior update during replay. See
/// <see cref="CategoricalContextBanditBaselineBase"/> for why the one-hot context lets this run without a
/// matrix inverse.
/// </summary>
public sealed class LinUcbBaseline : CategoricalContextBanditBaselineBase
{
    private readonly double _alpha;

    /// <summary>Initializes a new instance of the <see cref="LinUcbBaseline"/> class.</summary>
    /// <param name="alpha">α, the exploration-bonus weight. Canonical value <c>1</c>.</param>
    /// <param name="lambda">λ, the ridge prior weight. Canonical value <c>1</c>.</param>
    public LinUcbBaseline(double alpha = 1d, double lambda = 1d)
        : base(lambda)
    {
        _alpha = alpha;
    }

    /// <inheritdoc/>
    public override string Name => "linucb";

    /// <inheritdoc/>
    /// <remarks>
    /// The UCB score <c>θ_d + α·√(1/(λ+n_d))</c> — posterior mean plus an exploration bonus that shrinks as a pair
    /// accumulates pulls.
    /// </remarks>
    protected override double ScoreArm(double mean, double denominator)
    {
        return mean + _alpha * Math.Sqrt(1d / denominator);
    }
}