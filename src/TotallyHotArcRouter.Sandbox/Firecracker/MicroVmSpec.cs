using TotallyHot.ArcRouter.Sandbox.Execution;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>The specification for one microVM run: snippet, resource ceiling, and wall-clock timeout.</summary>
public sealed record MicroVmSpec
{
    /// <summary>The snippet language.</summary>
    public required SandboxLanguage Language { get; init; }

    /// <summary>The snippet source code.</summary>
    public required string Code { get; init; }

    /// <summary>The per-run working directory (API socket + CoW overlay live here).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The hard wall-clock ceiling in milliseconds.</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>The guest memory ceiling in bytes.</summary>
    public required long MemoryMaxBytes { get; init; }
}

/// <summary>
/// Runs a snippet inside a Firecracker microVM restored from a snapshot and returns its outcome. The Linux
/// implementation drives the Firecracker API and the in-guest exec agent; the runtime degrades when it is
/// not registered.
/// </summary>
public interface IMicroVmLauncher
{
    /// <summary>Runs the spec inside a microVM and returns the raw execution outcome.</summary>
    /// <param name="spec">The run specification.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The raw execution outcome.</returns>
    Task<ExecutionOutcome> RunAsync(MicroVmSpec spec, CancellationToken cancellationToken);
}

