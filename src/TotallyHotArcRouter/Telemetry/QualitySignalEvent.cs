namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One quality-verification signal, broadcast to connected dashboards as an optional live tile alongside
/// <see cref="RoutingTelemetryEvent"/>. Derived from a scored quality result; it is a <b>heuristic</b>
/// quality proxy for live traffic (no ground-truth tests), not a paper-grade metric.
/// </summary>
/// <remarks>
/// Every field is obtained by reading and parsing the model's answer, or by asking the shadow judge about
/// it. The execution-derived fields this event once carried - isolation tier, exit code, timed-out flag,
/// wall-clock duration, peak memory - are gone along with the executing verifier that produced them.
/// </remarks>
/// <param name="CorrelationId">Ties this signal to its <see cref="RoutingTelemetryEvent.CorrelationId"/>.</param>
/// <param name="SessionId">The routed request's session id.</param>
/// <param name="Dimension">The inferred task dimension (already live-namespaced by the observer).</param>
/// <param name="Model">The model the signal is attributed to.</param>
/// <param name="Language">The analyzed snippet's language.</param>
/// <param name="SyntaxValid">Whether the structural check passed.</param>
/// <param name="SyntaxAuthoritative">Whether a real parser, rather than a heuristic, produced that verdict.</param>
/// <param name="AnalysisScore">The composed static-analysis score in [0,1], or <see langword="null"/> when every analyzer abstained.</param>
/// <param name="JudgeScore">The G-Eval judge's grade in [0,1], or <see langword="null"/> when the judge did not contribute.</param>
/// <param name="UnifiedScore">The unified score u_i in [0,1] fed into router memory.</param>
/// <param name="DegradedReason">Why this signal carries less than a full grading, or null when it is complete.</param>
/// <param name="TimestampUtc">When the signal was produced.</param>
public sealed record QualitySignalEvent(
    string CorrelationId,
    string SessionId,
    string Dimension,
    string Model,
    string Language,
    bool SyntaxValid,
    bool SyntaxAuthoritative,
    double? AnalysisScore,
    double? JudgeScore,
    double UnifiedScore,
    string? DegradedReason,
    DateTimeOffset TimestampUtc);
