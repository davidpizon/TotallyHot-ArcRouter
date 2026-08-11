namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The <see cref="IRoutingPolicy"/> registered for live traffic: dispatches to
/// <see cref="UtilityRoutingPolicy"/> for requests PLAN.md Phase H's classifier marked
/// <see cref="RoutingContext.IsUtility"/>, and to <see cref="AgentRouterPolicy"/> otherwise
/// (<c>docs/router/utility-model-routing.md</c> §B3's tier split between the utility and general cases).
/// </summary>
public sealed class CompositeRoutingPolicy : IRoutingPolicy
{
    private readonly UtilityRoutingPolicy _utilityPolicy;
    private readonly AgentRouterPolicy _generalPolicy;

    /// <param name="utilityPolicy">Handles requests classified as utility traffic.</param>
    /// <param name="generalPolicy">Handles every other request.</param>
    public CompositeRoutingPolicy(UtilityRoutingPolicy utilityPolicy, AgentRouterPolicy generalPolicy)
    {
        ArgumentNullException.ThrowIfNull(utilityPolicy);
        ArgumentNullException.ThrowIfNull(generalPolicy);
        _utilityPolicy = utilityPolicy;
        _generalPolicy = generalPolicy;
    }

    /// <inheritdoc />
    public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.IsUtility
            ? _utilityPolicy.SelectModelAsync(context, cancellationToken)
            : _generalPolicy.SelectModelAsync(context, cancellationToken);
    }
}
