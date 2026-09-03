using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The frozen nine-dimension taxonomy's score ledger: the per-(dimension, model) predicted score that
/// <see cref="DimBestVoter"/> votes on, extracted here so
/// docs/router/self-organizing-classification-plan.md Phase T4's baseline comparison measures the
/// <em>same</em> prediction the voter actually casts. Keeping the blend rule in one place is the point -
/// a comparison that scored a taxonomy by a rule its voter does not use would measure nothing the router
/// does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Blend rule.</b> Prefer the live <see cref="RouterMemory"/> average when the (dimension, model) cell
/// has at least one observation; fall back to the offline CodeRouterBench probing prior otherwise. This is
/// <see cref="DimBestVoter"/>'s long-standing documented rule, moved rather than changed - Phase T4
/// measures the baseline as it is, and the plan's "the frozen baseline must remain frozen" boundary
/// forbids retuning it here.
/// </para>
/// <para>
/// <b>Dimension keys.</b> Every method takes the <see cref="RouterMemory"/> key
/// <em>
/// as the caller holds
/// it
/// </em>
/// and queries live memory with exactly that spelling; only the probing-prior lookup strips
/// <see cref="Quality.QualityOptions.LiveMemoryPrefix"/> back off, because the prior was built from
/// unprefixed CodeRouterBench rows. Prepending the prefix here instead would silently miss live scores
/// recorded under an unprefixed key, so the two sources are each queried under their own convention
/// rather than forced onto one. A caller holding a bare dimension - Phase T4's comparison job, reading
/// <c>request_transcripts.dimension</c> - converts it with
/// <see cref="Quality.RouterDimension.ToLiveKey"/> first, exactly as
/// <see cref="RouterMemoryScoreObserver"/> does when writing.
/// </para>
/// </remarks>
public sealed class DimensionLedger
{
    private readonly string _liveMemoryPrefix;
    private readonly DimensionModelScoreMatrix? _priorMatrix;
    private readonly RouterMemory _routerMemory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DimensionLedger"/> class.
    /// </summary>
    /// <param name="routerMemory">Live per-(dimension, model) score averages, preferred when present.</param>
    /// <param name="priorMatrix">
    /// The CodeRouterBench probing-split prior, or <see langword="null"/> when the corpus is not synced on
    /// this machine - in which case this ledger scores from live memory only, exactly as
    /// <see cref="DimBestVoter"/> already degrades.
    /// </param>
    /// <param name="liveMemoryPrefix">
    /// The <see cref="Quality.QualityOptions.LiveMemoryPrefix"/> applied to reach live-memory
    /// keys.
    /// </param>
    public DimensionLedger(RouterMemory routerMemory, DimensionModelScoreMatrix? priorMatrix, string liveMemoryPrefix)
    {
        ArgumentNullException.ThrowIfNull(routerMemory);
        ArgumentNullException.ThrowIfNull(liveMemoryPrefix);

        _routerMemory = routerMemory;
        _priorMatrix = priorMatrix;
        _liveMemoryPrefix = liveMemoryPrefix;
    }

    /// <summary>
    /// Returns this taxonomy's predicted score for <paramref name="model"/> under
    /// <paramref name="dimension"/>, or <see langword="null"/> when neither the live average nor the prior
    /// has a value for that cell.
    /// </summary>
    /// <param name="dimension">
    /// The live-memory dimension key, used verbatim; the prior lookup strips the live prefix if
    /// present.
    /// </param>
    /// <param name="model">Any spelling of a model id; matched through <c>ModelNameCanonicalizer</c> by both sources.</param>
    public double? Predict(string dimension, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return _routerMemory.GetAverageScore(dimension: dimension, model: model)
               ?? _priorMatrix?.AverageScore(dimension: BareDimension(dimension), model: model);
    }

    /// <summary>
    /// Returns this taxonomy's predicted score for <paramref name="model"/> under
    /// <paramref name="dimension"/> with <paramref name="observedScore"/> removed from the live average
    /// first - the held-out prediction Phase T4's mean-absolute-error comparison scores against.
    /// </summary>
    /// <param name="dimension">
    /// The live-memory dimension key, used verbatim; the prior lookup strips the live prefix if
    /// present.
    /// </param>
    /// <param name="model">The model whose cell the observation landed in.</param>
    /// <param name="observedScore">The observation to exclude, already folded into the live average by the time this runs.</param>
    /// <returns>
    /// The leave-one-out prediction, or <see langword="null"/> when no honest one exists - a live cell
    /// holding only this single observation leaves nothing behind to predict from, and is excluded from the
    /// error series rather than answered with a fabricated number.
    /// </returns>
    /// <remarks>
    /// By the time the comparison job runs, the verifier's score has already been folded into
    /// <see cref="RouterMemory"/>, so a plain <see cref="Predict"/> would be scoring each taxonomy partly
    /// against a number it had already absorbed - an optimistically biased error on both sides of the
    /// comparison, and therefore a biased input to the promotion criterion that reads it. Removing the
    /// observation from its own cell restores a genuine held-out error. The probing prior needs no such
    /// correction: it is offline CodeRouterBench data that live traffic never writes into, so it is
    /// returned as-is when the live cell cannot support a leave-one-out estimate.
    /// </remarks>
    public double? PredictLeaveOneOut(string dimension, string model, double observedScore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var prior = _priorMatrix?.AverageScore(dimension: BareDimension(dimension), model: model);

        var mean = _routerMemory.GetAverageScore(dimension: dimension, model: model);
        if (mean is null) return prior;

        var count = _routerMemory.GetObservationCount(dimension: dimension, model: model);
        return LeaveOneOutMean(mean: mean.Value, count: count, observedScore: observedScore) ?? prior;
    }

    /// <summary>
    /// Strips <see cref="_liveMemoryPrefix"/> from <paramref name="dimension"/> if present, recovering the
    /// unprefixed key the probing prior was built from.
    /// </summary>
    /// <param name="dimension">The dimension key as the caller holds it.</param>
    /// <returns>The key without the live-memory prefix.</returns>
    private string BareDimension(string dimension)
    {
        return dimension.StartsWith(value: _liveMemoryPrefix, comparisonType: StringComparison.Ordinal)
            ? dimension[_liveMemoryPrefix.Length..]
            : dimension;
    }

    /// <summary>
    /// Removes one observation from a mean, returning the mean of the remaining observations - the shared
    /// arithmetic behind both taxonomies' held-out predictions (see
    /// <see cref="ClusterLedger.PredictLeaveOneOut"/>).
    /// </summary>
    /// <param name="mean">The mean including <paramref name="observedScore"/>.</param>
    /// <param name="count">
    /// The number of observations behind <paramref name="mean"/>, including
    /// <paramref name="observedScore"/>.
    /// </param>
    /// <param name="observedScore">The observation to remove.</param>
    /// <returns>
    /// The mean of the other observations, or <see langword="null"/> when <paramref name="count"/> is 1 or
    /// less and removing this observation would leave nothing to average.
    /// </returns>
    internal static double? LeaveOneOutMean(double mean, int count, double observedScore)
    {
        return count <= 1 ? null : ((mean * count) - observedScore) / (count - 1);
    }
}