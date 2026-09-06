namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The cheap fields <see cref="PortfolioGraderDispatcher.DispatchAsync"/> snapshots from a scored
/// <see cref="Quality.QualityResult"/> before enqueuing one of Phase Q3's portfolio graders - mirrors
/// <see cref="JudgeShadowScoringJob"/> exactly, plus <see cref="GraderKey"/> to say which of
/// CodeJudge/ICE-Score/RACE this particular job is for. The raw response text is not carried here; the
/// drain worker recovers it separately from <see cref="PendingResponseTextCache"/>, keyed by
/// <see cref="CorrelationId"/>.
/// </summary>
/// <param name="CorrelationId">Correlation id shared with the response text cached in <see cref="PendingResponseTextCache"/>.</param>
/// <param name="GraderKey">Which portfolio grader this job is for, e.g. <see cref="Quality.GraderKeys.CodeJudge"/>.</param>
/// <param name="Dimension">The inferred task dimension.</param>
public sealed record PortfolioGraderJob(
    string CorrelationId,
    string GraderKey,
    string Dimension);
