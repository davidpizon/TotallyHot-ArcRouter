namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// A unit of work enqueued for off-path quality grading: one extracted code block plus the routing
/// context needed to attribute the resulting score back to a model and dimension.
/// </summary>
/// <param name="Code">The extracted source code (already size-capped by the extractor).</param>
/// <param name="Language">The detected language of <paramref name="Code"/>.</param>
/// <param name="Prompt">
/// The user prompt <paramref name="Code"/> was produced in answer to, or an empty string when the caller
/// could not supply it. Carried so a grader can judge the code <em>against its requirement</em> rather than
/// in isolation - see this type's remarks.
/// </param>
/// <param name="Dimension">The inferred task dimension (the paper's d(t_i)) used to key the score and weights.</param>
/// <param name="Model">The model that produced the response, used as the score's model key.</param>
/// <param name="CorrelationId">Ties the resulting signal back to its <c>RoutingTelemetryEvent</c>.</param>
/// <param name="SessionId">The routed request's session id, for correlation and logging.</param>
/// <remarks>
/// <b>Why <paramref name="Prompt"/> is here.</b> It was previously available at
/// <see cref="Ingress.QualityIngestContext"/> and <see cref="SignalExtractionContext"/>, consumed by
/// <see cref="IDimensionInferrer"/>, and then dropped at the extractor boundary - so every grader
/// downstream evaluated an answer without ever seeing the question. A complete, warning-free snippet that
/// answers a <em>different</em> question was indistinguishable from one that answers this one. Carrying the
/// prompt is also a hard prerequisite for the published reference-free scoring methods
/// (docs/research/code-quality-metrics-assessment.md), every one of which scores code against a stated
/// requirement.
/// </remarks>
public sealed record QualityRequest(
    string Code,
    CodeLanguage Language,
    string Prompt,
    string Dimension,
    string Model,
    string CorrelationId,
    string SessionId);