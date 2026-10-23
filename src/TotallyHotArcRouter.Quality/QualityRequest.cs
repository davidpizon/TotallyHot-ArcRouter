namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// A unit of work enqueued for off-path quality grading: either one extracted code block (the
/// <see cref="GradingRecipes.Code"/> recipe) or the full unfenced response text (the
/// <see cref="GradingRecipes.Bootstrap"/> recipe), plus the routing context needed to attribute the
/// resulting score back to a model.
/// </summary>
/// <param name="Code">
/// The extracted source code (already size-capped by the extractor). Empty on the bootstrap recipe,
/// which never runs the structural parser.
/// </param>
/// <param name="Language">The detected language of <paramref name="Code"/>, or <see cref="CodeLanguage.Unknown"/> on bootstrap.</param>
/// <param name="Prompt">
/// The user prompt the response was produced in answer to, or an empty string when the caller
/// could not supply it. Carried so a grader can judge the answer <em>against its requirement</em> rather than
/// in isolation - see this type's remarks.
/// </param>
/// <param name="Dimension">
/// The inferred task dimension (the paper's d(t_i)) used to key the score and weights on the code recipe.
/// Empty on bootstrap: that recipe is not a <c>RouterDimension</c> (ADR-0027).
/// </param>
/// <param name="Model">The model that produced the response, used as the score's model key.</param>
/// <param name="CorrelationId">Ties the resulting signal back to its <c>RoutingTelemetryEvent</c>.</param>
/// <param name="SessionId">The routed request's session id, for correlation and logging.</param>
/// <param name="ResponseText">
/// The unfenced assistant reply, carried on the bootstrap recipe so a grader can score prose without a
/// code block. Null on the code recipe: the judge recovers the full reply from the pending-text cache.
/// </param>
/// <param name="RecipeId">
/// Which weight vector to apply. Defaults to <see cref="GradingRecipes.Code"/> so existing call sites
/// keep the #293 0.60/0.25/0.15 mix.
/// </param>
/// <param name="IncompleteReason">
/// When set, ingress already knows the grade cannot complete (queue full, no text, empty). The worker
/// still submits so a receipt is written; it does not call the judge.
/// </param>
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
    string SessionId,
    string? ResponseText = null,
    string RecipeId = GradingRecipes.Code,
    string? IncompleteReason = null);
