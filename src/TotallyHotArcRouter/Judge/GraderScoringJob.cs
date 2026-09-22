namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The cheap fields <see cref="GraderDispatcher.DispatchAsync"/> snapshots from a scored
/// <see cref="TotallyHot.ArcRouter.Quality.QualityResult"/> before enqueuing one LLM grader. The raw response text is not
/// carried here; the drain worker recovers it from <see cref="PendingResponseTextCache"/>, keyed by
/// <see cref="CorrelationId"/>. <see cref="Model"/>/<see cref="StaticScore"/>/<see cref="SyntaxAuthoritative"/>
/// are used only by the G-Eval judge's post-score persist into <c>judge_shadow_scores</c> (G2); portfolio
/// jobs still snapshot them because the record is shared.
/// </summary>
/// <param name="CorrelationId">Correlation id shared with the cached response text.</param>
/// <param name="GraderKey">Which grader this job is for, e.g. <see cref="TotallyHot.ArcRouter.Quality.GraderKeys.Judge"/>.</param>
/// <param name="Dimension">The inferred task dimension.</param>
/// <param name="Model">The model that produced the evaluated response, snapshotted for the shadow row.</param>
/// <param name="StaticScore">The static verifier's unified score, recorded on the shadow row for agreement analysis.</param>
/// <param name="SyntaxAuthoritative">
/// Whether a real parser rather than a heuristic produced <paramref name="StaticScore"/>'s syntax verdict.
/// </param>
public sealed record GraderScoringJob(
    string CorrelationId,
    string GraderKey,
    string Dimension,
    string Model,
    double StaticScore,
    bool SyntaxAuthoritative);
