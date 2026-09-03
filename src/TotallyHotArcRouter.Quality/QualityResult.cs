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
    /// Bumped to <c>2.0</c> when execution-derived fields (tier, exit code, timeout/OOM/seccomp flags,
    /// captured output, wall-clock, peak memory) were removed along with the executing verifier itself.
    /// A consumer reading <c>1.0</c> rows out of historical telemetry must not expect these fields.
    /// </remarks>
    public string SchemaVersion { get; init; } = "2.0";

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
    /// Whether <see cref="SyntaxValid"/> came from a real parser for this language rather than the
    /// delimiter-balance heuristic. Consumers segmenting on signal strength need this: a non-authoritative
    /// verdict is a cheap guess, not a compiler's answer.
    /// </summary>
    public bool SyntaxAuthoritative { get; init; }

    /// <summary>
    /// The composed static-analysis score in [0,1], or <see langword="null"/> when no analyzer could
    /// contribute a finding for this snippet. Null drops the analysis axis from the score's normalization
    /// rather than scoring it zero.
    /// </summary>
    public double? AnalysisScore { get; init; }

    /// <summary>Human-readable findings from the static analyzers, retained for telemetry and diagnostics.</summary>
    public IReadOnlyList<string> AnalysisFindings { get; init; } = [];

    /// <summary>
    /// The G-Eval judge's grade in [0,1], or <see langword="null"/> when the judge was disabled, abstained,
    /// or did not answer before the join timeout. Null drops the judge axis from the normalization.
    /// </summary>
    public double? JudgeScore { get; init; }

    /// <summary>The unified score u_i in [0,1] that the router's memory learns from.</summary>
    public double UnifiedScore { get; init; }

    /// <summary>Why this result carries less signal than a full grading would, or null when it is complete.</summary>
    public string? DegradedReason { get; init; }
}