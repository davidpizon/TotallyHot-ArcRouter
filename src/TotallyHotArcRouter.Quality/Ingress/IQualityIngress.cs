namespace TotallyHot.ArcRouter.Quality.Ingress;

/// <summary>
/// The façade the proxy calls to feed a completed response into the quality verifier. It applies the sampling
/// decision, extracts a runnable snippet, and enqueues it — all non-blocking and best-effort so the
/// caller's hot path is never affected.
/// </summary>
public interface IQualityIngress
{
    /// <summary>Best-effort, non-blocking: samples, extracts, and enqueues a snippet from a response.</summary>
    /// <param name="context">The response text plus routing context.</param>
    void TryIngest(QualityIngestContext context);
}

/// <summary>Inputs to <see cref="IQualityIngress.TryIngest"/>.</summary>
/// <param name="ResponseText">The assistant's full reply text.</param>
/// <param name="Prompt">The newest user message, used for dimension inference.</param>
/// <param name="Model">The model that produced the response.</param>
/// <param name="CorrelationId">Correlation id linking back to the routing telemetry event.</param>
/// <param name="SessionId">The routed request's session id.</param>
public sealed record QualityIngestContext(
    string ResponseText,
    string Prompt,
    string Model,
    string CorrelationId,
    string SessionId);