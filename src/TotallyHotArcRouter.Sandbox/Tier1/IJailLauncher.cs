using TotallyHot.ArcRouter.Sandbox.Execution;

namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// The specification for one jailed run: interpreter, namespace flags, script location, resource limits,
/// and the hard wall-clock ceiling.
/// </summary>
public sealed record JailSpec
{
    /// <summary>The interpreter executable to run (e.g. <c>python3</c>).</summary>
    public required string Interpreter { get; init; }

    /// <summary>The <c>unshare(1)</c> namespace flags.</summary>
    public required IReadOnlyList<string> UnshareFlags { get; init; }

    /// <summary>The snippet file name within <see cref="WorkingDirectory"/>.</summary>
    public required string ScriptFileName { get; init; }

    /// <summary>The writable working directory (per-lease tmpfs) the snippet runs in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The hard wall-clock ceiling in milliseconds.</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>The <c>memory.max</c> value (bytes, decimal string).</summary>
    public required string MemoryMax { get; init; }

    /// <summary>The <c>pids.max</c> value (decimal string).</summary>
    public required string PidsMax { get; init; }

    /// <summary>The <c>cpu.max</c> value (quota and period).</summary>
    public required string CpuMax { get; init; }
}

/// <summary>
/// Launches a jailed interpreter run and returns its outcome. The Linux implementation applies namespaces,
/// cgroups, and an external supervisor; a null implementation lets the executor degrade off Linux.
/// </summary>
public interface IJailLauncher
{
    /// <summary>Runs the spec under isolation and returns the raw execution outcome.</summary>
    /// <param name="spec">The run specification.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The raw execution outcome.</returns>
    Task<ExecutionOutcome> RunAsync(JailSpec spec, CancellationToken cancellationToken);
}

