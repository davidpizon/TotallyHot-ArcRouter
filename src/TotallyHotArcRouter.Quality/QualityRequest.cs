namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// A unit of work enqueued for off-path quality grading: one extracted code block plus the routing
/// context needed to attribute the resulting score back to a model and dimension.
/// </summary>
/// <param name="Code">The extracted source code (already size-capped by the extractor).</param>
/// <param name="Language">The detected language of <paramref name="Code"/>.</param>
/// <param name="Dimension">The inferred task dimension (the paper's d(t_i)) used to key the score and weights.</param>
/// <param name="Model">The model that produced the response, used as the score's model key.</param>
/// <param name="CorrelationId">Ties the resulting signal back to its <c>RoutingTelemetryEvent</c>.</param>
/// <param name="SessionId">The routed request's session id, for correlation and logging.</param>
public sealed record QualityRequest(
    string Code,
    CodeLanguage Language,
    string Dimension,
    string Model,
    string CorrelationId,
    string SessionId);

