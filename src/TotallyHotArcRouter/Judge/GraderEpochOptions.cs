using System.ComponentModel.DataAnnotations;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Knobs for the phase 7 grader epoch (issue #304). Bound from the <c>GraderEpoch</c> section. A missing
/// section keeps these defaults. They are not on <see cref="JudgeOptions"/>, whose operational bounds are
/// not overridable from <c>appsettings.json</c>.
/// </summary>
public sealed class GraderEpochOptions
{
    /// <summary>The configuration section this type binds from.</summary>
    public const string SectionName = "GraderEpoch";

    /// <summary>
    /// Gets whether the epoch fits. When false, each tick logs that the job is off and does not fit.
    /// The tick still visits each kind for retirement and the spot-check window. A fresh install still
    /// skips the fit until enough teacher labels exist.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets whether a matching accepted artifact may skip the teacher. The default is on. False is the
    /// emergency off: the next grade calls the teacher, the artifact file stays, and <c>rollback.json</c>
    /// is not deleted. The 5% draw and the spot-check window still run.
    /// </summary>
    public bool TakeoverEnabled { get; init; } = true;

    /// <summary>
    /// Gets the fraction of eligible rows the teacher still grades after takeover. The draw is a hash
    /// of the correlation id. This is not <c>QualityOptions.SamplingRate</c>.
    /// </summary>
    [Range(0d, 1d)]
    public double SpotCheckRate { get; init; } = 0.05;

    /// <summary>
    /// Gets how many of the newest spot-check rows form the rollback window. A different counter from
    /// <see cref="HoldoutSize"/>. Fewer than this does not roll back.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SpotCheckWindowSize { get; init; } = 50;

    /// <summary>
    /// Gets the exclusive mean-absolute-error bar for the spot-check window. A mean miss greater than
    /// this returns that kind to the teacher. A mean miss equal to this stays.
    /// </summary>
    [Range(0d, 1d)]
    public double SpotCheckRollbackMae { get; init; } = 0.10;

    /// <summary>Gets how often the timer wakes, in minutes. The wake does not force a fit.</summary>
    [Range(1, 1440)]
    public int CheckIntervalMinutes { get; init; } = 5;

    /// <summary>
    /// Gets how many eligible teacher labels of one kind are required before every fit, including a
    /// refit while an accepted artifact still matches.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumLabels { get; init; } = 200;

    /// <summary>
    /// Gets how many training rows must remain after the holdout is taken and session siblings of
    /// holdout rows are excluded. Fewer than this skips the tick and does not shrink the holdout.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumTrainingRows { get; init; } = 150;

    /// <summary>Gets how many of the newest eligible labels of that kind form the holdout.</summary>
    [Range(1, int.MaxValue)]
    public int HoldoutSize { get; init; } = 50;

    /// <summary>Gets the inclusive holdout mean-absolute-error bar. Above this, the gate fails.</summary>
    [Range(0d, 1d)]
    public double MaxHoldoutMae { get; init; } = 0.10;

    /// <summary>
    /// Gets the inclusive Spearman bar. A null correlation, including zero variance in the holdout,
    /// fails the gate.
    /// </summary>
    [Range(-1d, 1d)]
    public double MinSpearman { get; init; } = 0.60;

    /// <summary>Gets how many paired-bootstrap resamples are drawn when a comparable live artifact exists.</summary>
    [Range(1, int.MaxValue)]
    public int BootstrapSamples { get; init; } = 2000;

    /// <summary>Gets the central bootstrap interval. The default tails are 2.5% and 97.5%.</summary>
    [Range(0.5d, 0.999d)]
    public double BootstrapConfidence { get; init; } = 0.95;

    /// <summary>
    /// Gets the <see cref="Random"/> seed for the reservoir sample and, separately, for the bootstrap.
    /// Each procedure constructs its own generator so the draws do not move each other.
    /// </summary>
    public int RandomSeed { get; init; } = 20261025;

    /// <summary>Gets how many question vectors are sealed into one artifact's fingerprint reservoir.</summary>
    [Range(1, int.MaxValue)]
    public int ReservoirCapacity { get; init; } = 5000;

    /// <summary>
    /// Gets the cosine-floor percentile, on a 0–100 scale. Phase 7 stores the floor and does not call
    /// the teacher because a row is below it.
    /// </summary>
    [Range(0, 100)]
    public int CosineFloorPercentile { get; init; } = 20;

    /// <summary>
    /// Gets the trainer name. Phase 7 fits only <c>Sdca</c>
    /// (<see cref="Microsoft.ML.Trainers.SdcaRegressionTrainer"/>). Any other name skips the fit.
    /// </summary>
    [Required]
    public string Trainer { get; init; } = GraderEpochTrainer.SdcaName;

    /// <summary>Gets how many failed candidates are kept per kind under <c>failed</c>.</summary>
    [Range(0, 1000)]
    public int FailedCandidateRetention { get; init; } = 3;
}
