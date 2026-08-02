namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Observes a scored <see cref="SandboxResult"/> into the router's learning memory. The host application
/// provides the concrete adapter (writing to a separate live-namespace store); the sandbox library only
/// depends on this seam so it never references the core router directly.
/// </summary>
public interface IRouterScoreObserver
{
    /// <summary>Observes a scored result.</summary>
    /// <param name="result">The scored result.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the observation is recorded.</returns>
    Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default);
}

