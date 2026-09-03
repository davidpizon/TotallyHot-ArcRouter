using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// Classifies an inbound request ahead of routing - PLAN.md Phase H, the Context leg of the C-A-F loop.
/// Implementations must stay on the hot path: no network call, no allocation-heavy scanning, bounded by
/// the request body's size.
/// </summary>
public interface IRequestClassifier
{
    /// <summary>Classifies a parsed request body.</summary>
    /// <param name="requestBody">The request's top-level JSON object.</param>
    /// <returns>The resulting <see cref="RequestClassification"/>.</returns>
    RequestClassification Classify(JsonObject requestBody);
}