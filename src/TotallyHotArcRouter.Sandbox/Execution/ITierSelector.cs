namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Picks the lowest isolation tier that satisfies a request's execution requirement, so most traffic
/// never pays microVM cost.
/// </summary>
public interface ITierSelector
{
    /// <summary>Selects the tier for a request.</summary>
    /// <param name="request">The request to evaluate.</param>
    /// <returns>The selected <see cref="SandboxTier"/>.</returns>
    SandboxTier Select(SandboxRequest request);
}

