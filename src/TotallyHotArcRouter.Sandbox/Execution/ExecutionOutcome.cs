namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// The raw result of running a snippet in a Tier 1/Tier 2 runtime, before it is folded into a
/// <see cref="SandboxResult"/> and scored.
/// </summary>
public sealed record ExecutionOutcome
{
    /// <summary>The process/VM exit code, or <see langword="null"/> if it never started.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether the supervisor killed the run for exceeding the wall-clock ceiling.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Whether the run was OOM-killed by the memory cgroup.</summary>
    public bool OomKilled { get; init; }

    /// <summary>Whether the run was terminated by a seccomp denial.</summary>
    public bool SeccompDenied { get; init; }

    /// <summary>Captured stdout (not yet size-capped or redacted).</summary>
    public string Stdout { get; init; } = string.Empty;

    /// <summary>Captured stderr (not yet size-capped or redacted).</summary>
    public string Stderr { get; init; } = string.Empty;

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long WallClockMs { get; init; }

    /// <summary>Peak resident memory in bytes, or 0 when not measured.</summary>
    public long PeakMemoryBytes { get; init; }
}

