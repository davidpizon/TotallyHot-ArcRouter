namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// A no-op observer used as a safe default when the host has not registered a real one. Keeps the sandbox
/// self-sufficient (e.g. in tests) without silently coupling to router memory.
/// </summary>
public sealed class NullRouterScoreObserver : IRouterScoreObserver
{
    /// <inheritdoc />
    public Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

