namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Runs a snippet inside one isolation tier and returns its raw execution outcome. Implementations are
/// registered per tier (Tier 1 native jail, Tier 2 Firecracker microVM); when none is registered for a
/// selected tier the executor degrades to a Tier-0 static result.
/// </summary>
public interface ISandboxRuntime
{
    /// <summary>The tier this runtime implements.</summary>
    SandboxTier Tier { get; }

    /// <summary>Executes <paramref name="request"/> under isolation and returns its raw outcome.</summary>
    /// <param name="request">The snippet and its context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The raw execution outcome.</returns>
    Task<ExecutionOutcome> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken);
}

