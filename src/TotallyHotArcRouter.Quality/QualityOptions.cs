namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Configuration for the off-path quality verifier, bound from the <c>Quality</c> section of
/// configuration. The verifier grades a model's response without ever running the code it contains, so
/// every setting here bounds analysis and queueing work rather than any kind of execution sandbox.
/// </summary>
public sealed class QualityOptions
{
    /// <summary>The configuration section name for quality-verifier settings.</summary>
    public const string SectionName = "Quality";

    /// <summary>Whether the quality verifier is enabled. When false nothing is extracted, enqueued, or graded.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Fraction of eligible requests actually graded (0..1). 1.0 grades every eligible request.</summary>
    public double SamplingRate { get; init; } = 1.0;

    /// <summary>Maximum characters of extracted code analyzed from a single block.</summary>
    public int MaxCodeBytes { get; init; } = 65536;

    /// <summary>Maximum number of fenced code blocks scanned per response.</summary>
    public int MaxCodeBlocks { get; init; } = 4;

    /// <summary>Capacity of the bounded work queue. Enqueues beyond this are dropped from sampling.</summary>
    public int QueueCapacity { get; init; } = 256;

    /// <summary>Maximum number of gradings processed concurrently by the worker.</summary>
    public int WorkerConcurrency { get; init; } = 2;

    /// <summary>
    /// Prefix applied to dimension keys when observing live scores into router memory, keeping heuristic
    /// live signals in a separate namespace from checked-in benchmark matrices.
    /// </summary>
    /// <remarks>
    /// The default is deliberately unchanged from the executing verifier this replaced: the persisted
    /// <c>(live:dimension, model)</c> score rows in router memory are keyed on this string, so altering it
    /// would orphan every score the router has already learned rather than migrating it.
    /// </remarks>
    public string LiveMemoryPrefix { get; init; } = "live:";

    /// <summary>
    /// How long the aggregator holds a completed static verdict waiting for the judge's grade before
    /// giving up and writing the static score alone, in milliseconds. Defaults to 60,000 (one minute) -
    /// generous, because the wait is entirely off the routing hot path, but bounded so a wedged judge
    /// backbone cannot pin held results in memory indefinitely.
    /// </summary>
    public int JudgeJoinTimeoutMs { get; init; } = 60_000;

    /// <summary>
    /// Maximum number of static verdicts held awaiting a judge grade before the oldest are evicted (and
    /// written static-only). Defaults to 2,000, matching <c>JudgeOptions.CacheCapacity</c> so the two
    /// sides of the join are sized alike.
    /// </summary>
    public int JudgeJoinCapacity { get; init; } = 2_000;

    /// <summary>
    /// Identifies the current scoring configuration, stamped onto each rescanned transcript row so the
    /// background rescan can tell rows it has already graded from rows graded by an older scorer.
    /// </summary>
    /// <remarks>
    /// <b>Bump this whenever a change would produce a different score for the same response</b> - a new
    /// grader, a changed weight, a reworded judge prompt. The rescan treats any row whose stamp differs
    /// from this value as needing a fresh grade, so leaving it unchanged after a scoring change silently
    /// freezes the corpus at the old scorer's verdicts, and bumping it needlessly re-grades every row (and,
    /// once LLM graders are registered, pays for every one of them again).
    /// </remarks>
    public string ScorerVersion { get; init; } = "2.0";

    /// <summary>Per-dimension scoring weights, keyed by dimension name.</summary>
    public Dictionary<string, DimensionWeightOptions> DimensionWeights { get; set; } =
        [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Resolves the scoring weights for a dimension, falling back to a balanced default.</summary>
    /// <param name="dimension">The dimension whose weights to resolve.</param>
    /// <returns>The configured weights, or <see cref="DimensionWeightOptions.Default"/>.</returns>
    public DimensionWeightOptions ResolveWeights(string dimension)
    {
        if (!string.IsNullOrEmpty(dimension) &&
            DimensionWeights.TryGetValue(key: dimension, value: out var weights)) return weights;

        return DimensionWeightOptions.Default;
    }
}

/// <summary>
/// Per-dimension weights for the three axes of the unified score. Weights need not pre-sum to 1: the
/// scorer normalizes by their total, and drops any axis that does not apply to a given result rather than
/// scoring it zero, so a missing signal never masquerades as a failing one.
/// </summary>
public sealed class DimensionWeightOptions
{
    /// <summary>The default balanced weighting used when a dimension has no explicit configuration.</summary>
    public static DimensionWeightOptions Default { get; } = new() { Syntax = 0.4, Analysis = 0.2, Judge = 0.4 };

    /// <summary>Weight applied to the structural-validity signal (s_syntax): does the snippet parse?</summary>
    public double Syntax { get; init; } = 0.4;

    /// <summary>
    /// Weight applied to the composed static-analysis signal (s_analysis): diagnostic severity, placeholder
    /// and truncation detection, and complexity - everything provable about the code without running it.
    /// </summary>
    public double Analysis { get; init; } = 0.2;

    /// <summary>
    /// Weight applied to the G-Eval judge's grade (s_judge). Dropped from the normalization whenever the
    /// judge is disabled, abstains, or does not answer within
    /// <see cref="QualityOptions.JudgeJoinTimeoutMs"/>, so a static-only score spans the full [0,1] range
    /// rather than being capped by an axis that could never be filled.
    /// </summary>
    public double Judge { get; init; } = 0.4;
}