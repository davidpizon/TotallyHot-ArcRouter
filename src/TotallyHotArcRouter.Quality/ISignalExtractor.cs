namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Extracts a runnable code snippet (and its routing context) from an assistant response, applying size
/// and count caps. Runs in the trusted host after the response has been streamed to the client.
/// </summary>
public interface ISignalExtractor
{
    /// <summary>
    /// Attempts to extract the first runnable code block from <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The response text plus routing context.</param>
    /// <returns>A <see cref="QualityRequest"/> when a block was found; otherwise <see langword="null"/>.</returns>
    QualityRequest? Extract(SignalExtractionContext context);
}

/// <summary>
/// Inputs to <see cref="ISignalExtractor.Extract"/>: the response to mine plus the context needed to
/// attribute any resulting score.
/// </summary>
/// <param name="ResponseText">The assistant's reply text.</param>
/// <param name="Prompt">The user prompt, used for dimension inference.</param>
/// <param name="Model">The model that produced the response.</param>
/// <param name="CorrelationId">Correlation id linking back to the routing telemetry event.</param>
/// <param name="SessionId">The routed request's session id.</param>
public sealed record SignalExtractionContext(
    string ResponseText,
    string Prompt,
    string Model,
    string CorrelationId,
    string SessionId);