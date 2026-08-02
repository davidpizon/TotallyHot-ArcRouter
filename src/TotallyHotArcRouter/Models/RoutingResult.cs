namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Represents the result of a routing operation, including the decision and the response.
/// </summary>
public class RoutingResult
{
    /// <summary>
    /// Gets or sets the routing decision.
    /// </summary>
    public required RoutingDecision Decision { get; init; }

    /// <summary>
    /// Gets or sets the model's response.
    /// </summary>
    public required string Response { get; init; }
}

