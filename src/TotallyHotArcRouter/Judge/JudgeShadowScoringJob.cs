namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The cheap fields <see cref="JudgeShadowScoreDispatcher.DispatchAsync"/> snapshots from a scored
/// <see cref="Quality.QualityResult"/> before enqueuing - the raw response text is not carried here; the
/// drain worker recovers it separately from <see cref="PendingResponseTextCache"/>, keyed by
/// <see cref="CorrelationId"/>, at the point it actually calls the judge.
/// </summary>
/// <param name="CorrelationId">
/// Correlation id shared with the response text cached in
/// <see cref="PendingResponseTextCache"/>.
/// </param>
/// <param name="Dimension">The inferred task dimension, used to select G-Eval criteria.</param>
/// <param name="Model">The model that produced the evaluated response.</param>
/// <param name="StaticScore">
/// The static verifier's unified score for this result, recorded alongside the judge's opinion
/// for later agreement analysis.
/// </param>
public sealed record JudgeShadowScoringJob(
    string CorrelationId,
    string Dimension,
    string Model,
    double StaticScore);