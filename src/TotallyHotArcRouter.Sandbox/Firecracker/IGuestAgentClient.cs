namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>The result returned by the in-guest exec agent for one snippet run.</summary>
/// <param name="ExitCode">The interpreter's exit code.</param>
/// <param name="Stdout">Captured stdout.</param>
/// <param name="Stderr">Captured stderr.</param>
/// <param name="TimedOut">Whether the guest agent aborted the run for exceeding its own deadline.</param>
/// <param name="PeakMemoryBytes">Peak resident memory the guest observed, or 0 when unavailable.</param>
public sealed record GuestExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    long PeakMemoryBytes);

/// <summary>
/// Sends a snippet to the in-guest exec agent over the microVM's vsock channel and returns its result.
/// Abstracted so the Tier-2 orchestration is unit-testable without a running guest.
/// </summary>
public interface IGuestAgentClient
{
    /// <summary>Executes a snippet inside the guest via the exec agent.</summary>
    /// <param name="vsockUdsPath">The host-side Unix socket Firecracker exposes for the guest vsock.</param>
    /// <param name="port">The vsock port the guest agent listens on.</param>
    /// <param name="language">The snippet language.</param>
    /// <param name="code">The snippet source.</param>
    /// <param name="timeoutMs">The wall-clock ceiling in milliseconds.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The guest execution result.</returns>
    Task<GuestExecResult> ExecuteAsync(
        string vsockUdsPath,
        uint port,
        SandboxLanguage language,
        string code,
        int timeoutMs,
        CancellationToken cancellationToken);
}

