using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Placeholder <see cref="IRouterModelClient"/> registered so the DI container has a resolvable
/// dependency graph even though the router/agent subsystem has no real model backend wired up yet.
/// <see cref="AgentAsARouter"/> is registered as Transient by <c>AddTotallyHotArcRouter</c>, and
/// <c>ServiceProviderOptions.ValidateOnBuild</c> (enabled by <c>Host.CreateDefaultBuilder</c> in the
/// Development environment) eagerly checks every registered service's constructor dependencies at
/// startup, regardless of whether that service is ever actually resolved at runtime.
/// </summary>
public sealed class NotImplementedRouterModelClient : IRouterModelClient
{
    private readonly ILogger<NotImplementedRouterModelClient> _logger;

    /// <param name="logger">Used to log an error each time <see cref="GetResponseAsync"/> is invoked.</param>
    public NotImplementedRouterModelClient(ILogger<NotImplementedRouterModelClient> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="NotImplementedException">Always faults: no real model backend is configured.</exception>
    public Task<string> GetResponseAsync(string model, string prompt, CancellationToken cancellationToken = default)
    {
        _logger.LogError(
            "NotImplementedRouterModelClient.GetResponseAsync was invoked for model '{Model}', but no real model backend is wired up yet; this is only a DI-validation placeholder.",
            model);

        return Task.FromException<string>(new NotImplementedException(
            "No real model backend is wired up for IRouterModelClient. Register a concrete implementation before using AgentAsARouter."));
    }
}

