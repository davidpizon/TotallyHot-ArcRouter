namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The general (non-utility) <see cref="IRoutingPolicy"/> for the <c>agentic-router</c> alias
/// (<c>docs/router/utility-model-routing.md</c> §B3's "General" case): delegates the choice to
/// <see cref="AgentAsARouter"/>'s selection-only engine.
/// </summary>
public sealed class AgentRouterPolicy : IRoutingPolicy
{
    private readonly AgentAsARouter _router;

    /// <param name="router">The selection-only engine this policy delegates to.</param>
    public AgentRouterPolicy(AgentAsARouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
    }

    /// <inheritdoc />
    public async Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Candidates.Count == 0)
        {
            throw new InvalidOperationException("AgentRouterPolicy requires at least one candidate.");
        }

        var decision = await _router.SelectModelAsync(context.Dimension, cancellationToken);

        // Ensure the selected model is in the candidates list to satisfy IRoutingPolicy contract.
        // Use case-insensitive comparison to match ModelRouteResolver resolution behavior.
        var selectedModel = decision.SelectedModel;
        if (context.Candidates.Any(c => c.ModelName.Equals(selectedModel, StringComparison.OrdinalIgnoreCase)))
        {
            return selectedModel;
        }

        // Fallback to first candidate if selection is not eligible.
        return context.Candidates[0].ModelName;
    }
}
