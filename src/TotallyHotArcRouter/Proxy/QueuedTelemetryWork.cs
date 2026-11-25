namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// The facts <see cref="RequestTelemetryPublisher"/> computed for one served request before any SQLite write.
/// The background writer applies them after the response is complete. It holds no <see cref="HttpContext"/>:
/// Kestrel may reuse the context once the middleware returns.
/// </summary>
/// <param name="Turn">Identity, usage, cost, and text extracts.</param>
/// <param name="Route">The route that served the request.</param>
/// <param name="RequestedModelName">The model name the client sent.</param>
/// <param name="TaskText">Classification task text, when the router computed one.</param>
/// <param name="HasToolCalls">Whether the reply contained tool calls, or null when the body was not parsed.</param>
/// <param name="FollowsToolResults">Whether the newest turn is only tool results.</param>
/// <param name="DimBestModel">The dimension-best model, when one was chosen.</param>
/// <param name="UntrainedBaselineModel">The untrained baseline model, when one was scored.</param>
/// <param name="UntrainedBaselinePredictedScore">That baseline's predicted score.</param>
/// <param name="UsageExtracted">Whether a usage block was read from the response.</param>
/// <param name="RouterTokens">Tokens the router itself spent on this request.</param>
/// <param name="RequestSummary">The truncated request summary for the live event.</param>
/// <param name="ResponseSummary">The truncated response summary for the live event.</param>
/// <param name="UpstreamRequestId">The upstream <c>request-id</c> or <c>x-request-id</c>, copied while the response headers still exist.</param>
/// <param name="ResolveArchive">Binds the transcript row to the session file. Already invoked on the request thread when capture is on.</param>
internal sealed record QueuedTelemetryWork(
    PublishedTurn Turn,
    ResolvedModelRoute Route,
    string RequestedModelName,
    string? TaskText,
    bool? HasToolCalls,
    bool FollowsToolResults,
    string? DimBestModel,
    string? UntrainedBaselineModel,
    double? UntrainedBaselinePredictedScore,
    bool UsageExtracted,
    int RouterTokens,
    string? RequestSummary,
    string? ResponseSummary,
    string? UpstreamRequestId,
    Func<string, ArchiveBinding>? ResolveArchive);
