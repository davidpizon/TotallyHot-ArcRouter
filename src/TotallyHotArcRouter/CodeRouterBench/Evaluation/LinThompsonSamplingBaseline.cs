namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The LinTS baseline (research-doc Table 4), categorical-context variant: one-hot over dimension, the
/// canonical parameters <c>v = 0.5, λ = 1</c>, warm-started and replayed with a seeded RNG for
/// reproducibility. See <see cref="CategoricalContextBanditBaselineBase"/> for why the one-hot context
/// collapses the posterior covariance to one scalar variance per (arm, dimension) pair.
/// </summary>
public sealed class LinThompsonSamplingBaseline : CategoricalContextBanditBaselineBase
{
    private readonly Random _random;
    private readonly double _v;

    /// <summary>Initializes a new instance of the <see cref="LinThompsonSamplingBaseline"/> class.</summary>
    /// <param name="v">The posterior-variance scale. Canonical value <c>0.5</c>.</param>
    /// <param name="lambda">λ, the ridge prior weight. Canonical value <c>1</c>.</param>
    /// <param name="seed">
    /// The RNG seed for the Gaussian posterior draws, shared across warm-start and the scored replay so a
    /// run is fully reproducible end to end. Canonical value <c>42</c> (research-doc Table 4).
    /// </param>
    public LinThompsonSamplingBaseline(double v = 0.5, double lambda = 1d, int seed = 42)
        : base(lambda)
    {
        _v = v;
        _random = new Random(seed);
    }

    /// <inheritdoc/>
    public override string Name => "lints";

    /// <inheritdoc/>
    /// <remarks>
    /// Draws one sample from <c>N(θ_d, v²/(λ+n_d))</c> per candidate and lets the draw itself pick the arm — Thompson
    /// sampling's exploration, not an explicit bonus term.
    /// </remarks>
    protected override double ScoreArm(double mean, double denominator)
    {
        var standardDeviation = _v * Math.Sqrt(1d / denominator);
        return mean + standardDeviation * SampleStandardNormal();
    }

    private double SampleStandardNormal()
    {
        // Box-Muller transform over Random.NextDouble(); u1 is nudged off zero so Math.Log never sees 0.
        var u1 = 1d - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2d * Math.Log(u1)) * Math.Cos(2d * Math.PI * u2);
    }
}