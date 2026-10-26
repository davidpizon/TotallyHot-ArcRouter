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
    public string ScorerVersion { get; init; } = "3.0";

    /// <summary>
    /// Identifies the weight table version stamped onto grade receipts so an audit can tell which fixed
    /// weights produced a score. Bump when <see cref="DimensionWeights"/> defaults change.
    /// </summary>
    public string WeightsVersion { get; init; } = "3.0-judge-heavy";

    /// <summary>
    /// Midpoint of the normalized score used as the contradiction cutoff for user signals (issue #304
    /// phase 4). An agent switch against a grade at or above this value, or thanks against a grade below
    /// it, marks the receipt <c>disputed</c>.
    /// </summary>
    public double ContradictionCutoff { get; init; } = 0.5;

    /// <summary>
    /// Maximum trimmed character length for the thanks detector (issue #304 phase 4). Longer messages
    /// are never treated as thanks.
    /// </summary>
    public int ThanksMaxChars { get; init; } = 80;

    /// <summary>Per-dimension scoring weights, keyed by dimension name.</summary>
    public Dictionary<string, DimensionWeightOptions> DimensionWeights { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the scoring weights for a dimension and recipe. The bootstrap recipe is judge-only and
    /// is not looked up in <see cref="DimensionWeights"/> (ADR-0027).
    /// </summary>
    /// <param name="dimension">The dimension whose weights to resolve (ignored for bootstrap).</param>
    /// <param name="recipeId">
    /// The grading recipe. <see cref="GradingRecipes.Bootstrap"/> returns
    /// <see cref="DimensionWeightOptions.Bootstrap"/>; anything else uses the dimension table.
    /// </param>
    /// <returns>The configured weights, or <see cref="DimensionWeightOptions.Default"/>.</returns>
    public DimensionWeightOptions ResolveWeights(string dimension, string? recipeId = null)
    {
        if (string.Equals(recipeId, GradingRecipes.Bootstrap, StringComparison.OrdinalIgnoreCase))
            return DimensionWeightOptions.Bootstrap;

        if (!string.IsNullOrEmpty(dimension) &&
            DimensionWeights.TryGetValue(key: dimension, value: out var weights)) return weights;

        return DimensionWeightOptions.Default;
    }

    /// <summary>
    /// Ensures every configured dimension's weights (and the default and bootstrap recipes) sum to 1
    /// within 1e-9. Called at host startup so a misconfigured table fails fast rather than silently
    /// shifting effective weights.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when any dimension's weights do not sum to 1.</exception>
    public void EnsureDimensionWeightsSumToOne()
    {
        DimensionWeightOptions.Default.EnsureSumsToOne(dimensionLabel: "(default)");
        DimensionWeightOptions.Bootstrap.EnsureSumsToOne(dimensionLabel: GradingRecipes.Bootstrap);
        foreach (var (dimension, weights) in DimensionWeights)
            weights.EnsureSumsToOne(dimensionLabel: dimension);
    }
}

/// <summary>
/// Per-dimension fixed weights for the unified score. <see cref="Syntax"/> + <see cref="Analysis"/> +
/// <see cref="Judge"/> + Σ<see cref="ExtraWeights"/> must equal 1.0 (ADR-0026 / Eq. 8).
/// </summary>
public sealed class DimensionWeightOptions
{
    /// <summary>Absolute tolerance when checking that weights sum to 1.</summary>
    private const double SumTolerance = 1e-9;

    /// <summary>
    /// The default judge-heavy weighting used when a dimension has no explicit configuration
    /// (issue #293 starting weights).
    /// </summary>
    public static DimensionWeightOptions Default { get; } = new() { Syntax = 0.25, Analysis = 0.15, Judge = 0.60 };

    /// <summary>
    /// The temporary non-code recipe (issue #304 / ADR-0027): judge 1.0, syntax 0, analysis 0. Not a
    /// <c>RouterDimension</c>. Stamped on receipts as <c>weights_version = bootstrap</c>.
    /// </summary>
    public static DimensionWeightOptions Bootstrap { get; } = new() { Syntax = 0.0, Analysis = 0.0, Judge = 1.0 };

    /// <summary>Weight applied to the structural-validity signal (s_syntax): does the snippet parse?</summary>
    public double Syntax { get; init; } = 0.25;

    /// <summary>
    /// Weight applied to the composed static-analysis signal (s_analysis): diagnostic severity, placeholder
    /// and truncation detection, and complexity - everything provable about the code without running it.
    /// </summary>
    public double Analysis { get; init; } = 0.15;

    /// <summary>
    /// Weight applied to the G-Eval judge's grade (s_judge). When the judge is absent the grade status
    /// becomes <see cref="GradeStatus.StructureOnly"/> or <see cref="GradeStatus.Incomplete"/>; the weight
    /// itself is never dropped or renormalized.
    /// </summary>
    public double Judge { get; init; } = 0.60;

    /// <summary>
    /// Weights for graders beyond the three built-in axes above, keyed by grader name and matched against
    /// <see cref="QualityResult.GraderScores"/>. Must be included in the sum-to-1 check. Leave at zero
    /// unless the corresponding portfolio grader is enabled and expected to always join.
    /// </summary>
    public IReadOnlyDictionary<string, double> ExtraWeights { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves the configured weight for an extra grader, or zero when it has none.</summary>
    /// <param name="graderKey">The grader's key, matched against <see cref="QualityResult.GraderScores"/>.</param>
    /// <returns>The configured weight, or <c>0.0</c> when the grader is unconfigured.</returns>
    public double ResolveExtraWeight(string graderKey)
    {
        return ExtraWeights.GetValueOrDefault(key: graderKey, defaultValue: 0.0);
    }

    /// <summary>Sum of syntax, analysis, judge, and every extra weight.</summary>
    /// <returns>The total weight.</returns>
    private double TotalWeight()
    {
        var total = Syntax + Analysis + Judge;
        foreach (var weight in ExtraWeights.Values) total += weight;
        return total;
    }

    /// <summary>Throws when <see cref="TotalWeight"/> is not within <see cref="SumTolerance"/> of 1.0.</summary>
    /// <param name="dimensionLabel">Dimension name used in the exception message.</param>
    public void EnsureSumsToOne(string dimensionLabel)
    {
        var total = TotalWeight();
        if (Math.Abs(total - 1.0) > SumTolerance)
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Quality dimension '{dimensionLabel}' weights must sum to 1.0 (±{SumTolerance}); got {total}."));
    }
}