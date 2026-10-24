namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// The graded outcome of one <see cref="QualityRequest"/>: the structural verdict, the composed
/// static-analysis findings, the optional judge grade, and the unified score they collapse into. Every
/// field is derived by reading and parsing the code - nothing here was obtained by running it.
/// </summary>
public sealed record QualityResult
{
    /// <summary>The schema version of this result's shape, stamped onto telemetry for forward compatibility.</summary>
    /// <remarks>
    /// Bumped to <c>3.0</c> with issue #293: <see cref="GradeStatus"/>, fixed weights, and
    /// <see cref="SyntaxConfidence"/> change what a score means. A consumer reading <c>2.x</c> rows must
    /// not assume those fields.
    /// </remarks>
    // ReSharper disable once UnusedMember.Global
    public string SchemaVersion { get; init; } = "3.0";

    /// <summary>Correlation id tying this result back to its <c>RoutingTelemetryEvent</c>.</summary>
    public string RequestCorrelationId { get; init; } = string.Empty;

    /// <summary>The routed request's session id.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The inferred task dimension this result is keyed under.</summary>
    public string Dimension { get; init; } = string.Empty;

    /// <summary>The model that produced the graded response.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The detected language of the graded snippet.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Whether the snippet parsed cleanly.</summary>
    public bool SyntaxValid { get; init; }

    /// <summary>
    /// Whether <see cref="SyntaxValid"/> came from a real parser for this language rather than a heuristic.
    /// </summary>
    public bool SyntaxAuthoritative { get; init; }

    /// <summary>
    /// How sure the syntax checker is, in <c>[0, 1]</c>. Below
    /// <see cref="Grading.GradeStatusCalculator.MinimumSyntaxConfidenceForComplete"/> the grade cannot be
    /// <see cref="GradeStatus.Complete"/>.
    /// </summary>
    public double SyntaxConfidence { get; init; } = 1.0;

    /// <summary>
    /// Whether a non-empty user/task question was available when this result was graded. Required for
    /// <see cref="GradeStatus.Complete"/>.
    /// </summary>
    public bool QuestionPresent { get; init; }

    /// <summary>
    /// Whether this grade may train the router. Only <see cref="GradeStatus.Complete"/> does
    /// (ADR-0026).
    /// </summary>
    public GradeStatus GradeStatus { get; init; } = GradeStatus.Incomplete;

    /// <summary>
    /// The composed static-analysis score in <c>[0, 1]</c>, or <see langword="null"/> when no analyzer could
    /// contribute. A missing analysis axis with nonzero weight yields <see cref="GradeStatus.Incomplete"/>;
    /// the scorer still contributes <c>0</c> for that axis rather than renormalizing.
    /// </summary>
    public double? AnalysisScore { get; init; }

    /// <summary>Human-readable findings from the static analyzers, retained for telemetry and diagnostics.</summary>
    public IReadOnlyList<string> AnalysisFindings { get; init; } = [];

    /// <summary>
    /// The G-Eval judge's grade in <c>[0, 1]</c>, or <see langword="null"/> when the judge was disabled,
    /// abstained, or did not answer before the join timeout.
    /// </summary>
    public double? JudgeScore { get; init; }

    /// <summary>
    /// The unified score <c>u_i</c> in <c>[0, 1]</c>: the fixed-weight sum. Display-only unless
    /// <see cref="GradeStatus"/> is <see cref="GradeStatus.Complete"/>.
    /// </summary>
    public double UnifiedScore { get; init; }

    /// <summary>Why this result carries less signal than a full grading would, or null when it is complete.</summary>
    public string? DegradedReason { get; init; }

    /// <summary>
    /// Contributions from graders beyond the three built-in axes, keyed by grader name.
    /// </summary>
    public IReadOnlyDictionary<string, double> GraderScores { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-grader reasons an extra grader abstained or did not answer, keyed by grader name.
    /// </summary>
    public IReadOnlyDictionary<string, string> GraderDegradedReasons { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which grading recipe produced this result. Empty or <see cref="GradingRecipes.Code"/> is the #293
    /// code mix; <see cref="GradingRecipes.Bootstrap"/> is judge-only and never trains a
    /// <c>RouterDimension</c> tally (ADR-0027).
    /// </summary>
    public string RecipeId { get; init; } = GradingRecipes.Code;

    /// <summary>
    /// Why the grade is <see cref="GradeStatus.Incomplete"/>, or null when it is complete or the reason
    /// already lives in <see cref="GraderDegradedReasons"/>. Written onto the receipt as
    /// <c>incomplete_reason</c>.
    /// </summary>
    public string? IncompleteReason { get; init; }

    /// <summary>
    /// Who produced the grade of record. <see cref="GradeSources.Teacher"/> when the pinned judge filled
    /// <see cref="JudgeScore"/> on a complete grade; null when no score was produced.
    /// </summary>
    public string? GradeSource { get; init; }

    /// <summary>Whether the graded reply contained a fenced code block.</summary>
    public bool ResponseHasCode { get; init; }

    /// <summary>
    /// The pinned judge model that produced <see cref="JudgeScore"/>, or null when the judge did not run.
    /// </summary>
    public string? JudgeModel { get; init; }

    /// <summary>
    /// The local grader's normalized score, or null when that grader did not run. A value here is not
    /// <see cref="JudgeScore"/>. Phase 3's response-vector write treats a set value as "a grader scored
    /// this reply," including a shadow score whose <see cref="JudgeScore"/> is empty.
    /// </summary>
    public double? LocalScore { get; init; }

    /// <summary>
    /// Whether the pending question vector was missing at grade time. <see langword="true"/> is a
    /// <c>TryPeek</c> miss (expired, evicted, or never stored). <see langword="false"/> is a hit.
    /// Null until the grade-time reader stamps it. Mutable because that reader and the receipt observer
    /// share this instance, and the reader must stamp the flag before the kNN observer takes the vector.
    /// </summary>
    public bool? QuestionVectorMissing { get; set; }

    /// <summary>
    /// Character length of the graded response text, stored on the receipt and reported for verbosity
    /// audits. Never added into <see cref="UnifiedScore"/> (issue #304 phase 4).
    /// </summary>
    public int? ResponseLengthChars { get; init; }

    /// <summary>
    /// Whether this row was drawn for the random teacher spot-check. Phase 4 only persists the column;
    /// the 5% draw stays phase 8, so grade-time writes leave this false.
    /// </summary>
    public bool SpotCheck { get; init; }

    /// <summary>
    /// A counted user signal later attached to this grade (<see cref="UserSignals"/>), or null when none
    /// has been recorded yet.
    /// </summary>
    public string? UserSignal { get; init; }
}
