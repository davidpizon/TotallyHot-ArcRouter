namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Evaluates a single <see cref="SandboxRequest"/> end to end: structural parse, tier selection,
/// (optional) isolated execution, and scoring into a populated <see cref="SandboxResult"/>.
/// </summary>
public interface ISandboxExecutor
{
    /// <summary>Evaluates a request and returns its scored result.</summary>
    /// <param name="request">The request to evaluate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The populated, scored result.</returns>
    Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken);
}

